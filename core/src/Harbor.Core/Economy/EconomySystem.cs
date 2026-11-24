using Harbor.Core.Math;
using Harbor.Core.Sim;

namespace Harbor.Core.Economy;

/// <summary>Server-only economy (ADR 0005): profiles, ledger, shop, laundering, safehouse stash,
/// crew vault, death cash drops, payouts with diminishing returns. Every money movement is a ledger
/// transfer; client retries are idempotent through the request nonce in the TxId.</summary>
public sealed class EconomySystem : ISimSystem
{
    public EconomyData Data { get; }
    public Ledger Ledger { get; } = new();
    public CrewRegistry Crews { get; } = new();
    public PayoutGovernor Governor { get; }
    public IItemReceiver? Items { get; set; }
    public double Now { get; private set; }

    private readonly Dictionary<string, PlayerProfile> _profiles = new();
    private readonly Dictionary<int, PlayerProfile> _byActor = new();
    private readonly Dictionary<string, string> _businessOwner = new();
    private readonly Dictionary<string, Queue<LaunderJob>> _launderQueues = new();
    private readonly Dictionary<string, double> _launderCarry = new();
    private readonly Dictionary<int, CashDrop> _drops = new();
    private SimWorld _world = null!;
    private int _nextDrop = 1;
    private long _txCounter;

    private sealed class LaunderJob(string profile, long remaining)
    {
        public string Profile { get; } = profile;
        public long Total { get; } = remaining;
        public long Remaining { get; set; } = remaining;
        public long FeeCharged { get; set; }
    }

    private sealed record CashDrop(int Id, int LootActorId, Vec3d Position, double ExpiresAt, double PriorityUntil, int PriorityCrew, string PriorityProfile);

    public EconomySystem(EconomyData data)
    {
        Data = data;
        Governor = new PayoutGovernor(data.Config.DiminishingReturns);
        Ledger.Posted += OnPosted;
    }

    public void Initialize(SimWorld world) => _world = world;

    // ------------------------------------------------------------------ profiles

    public PlayerProfile GetOrCreateProfile(string id, string name)
    {
        if (_profiles.TryGetValue(id, out var p)) return p;
        p = new PlayerProfile(id, name);
        _profiles[id] = p;
        var s = Data.Config.StartingBalance;
        if (s.Clean > 0) Ledger.Post(new Transfer($"start:{id}:clean", AccountId.Faucet("starting"), AccountId.Bank(id), s.Clean, Currency.Clean, "starting"));
        if (s.Dirty > 0) Ledger.Post(new Transfer($"start:{id}:dirty", AccountId.Faucet("starting"), AccountId.Wallet(id), s.Dirty, Currency.Dirty, "starting"));
        return p;
    }

    public void BindActor(int actorId, PlayerProfile profile) => _byActor[actorId] = profile;
    public void UnbindActor(int actorId) => _byActor.Remove(actorId);
    public PlayerProfile? ProfileOf(int actorId) => _byActor.GetValueOrDefault(actorId);
    public PlayerProfile? Profile(string id) => _profiles.GetValueOrDefault(id);
    public int? ActorOf(string profileId) => _byActor.Where(kv => kv.Value.Id == profileId).Select(kv => (int?)kv.Key).FirstOrDefault();

    public long Wallet(string profile) => Ledger.Balance(AccountId.Wallet(profile), Currency.Dirty);
    public long Bank(string profile) => Ledger.Balance(AccountId.Bank(profile), Currency.Clean);
    public long Stash(string profile) => Ledger.Balance(AccountId.Stash(profile), Currency.Dirty);

    private string NextTx(string prefix) => $"{prefix}:{_world?.Loop.Tick ?? 0}:{++_txCounter}";

    // ------------------------------------------------------------------ payouts

    /// <summary>Pays an activity reward from a faucet, applying diminishing returns per (player, activity).</summary>
    public long Payout(string profileId, Currency currency, long baseAmount, string activity, string txId)
    {
        if (baseAmount <= 0 || Ledger.WasApplied(txId)) return 0;
        var mul = Governor.Multiplier(profileId, activity, Now);
        var amount = (long)System.Math.Round(baseAmount * mul);
        var to = currency == Currency.Dirty ? AccountId.Wallet(profileId) : AccountId.Bank(profileId);
        if (!Ledger.Post(new Transfer(txId, AccountId.Faucet(activity), to, amount, currency, activity)).Ok) return 0;
        Governor.Record(profileId, activity, Now);
        _world.Events.Publish(new PayoutGranted(profileId, currency, amount, activity, mul));
        return amount;
    }

    /// <summary>Moves money between two accounts (used by activities, e.g. heist split from crew vault).</summary>
    public TxResult Move(string txId, AccountId from, AccountId to, long amount, Currency c, string reason)
        => Ledger.Post(new Transfer(txId, from, to, amount, c, reason));

    // ------------------------------------------------------------------ shop

    public EconResult Buy(int actorId, string itemId, Currency currency, ulong nonce)
    {
        if (!_byActor.TryGetValue(actorId, out var p)) return EconResult.Fail("no_profile");
        if (!Data.Shop.TryGetValue(itemId, out var item)) return EconResult.Fail("unknown_item");
        if (!item.Currencies.Contains(currency)) return EconResult.Fail("currency_not_accepted");
        if (p.Rep < item.MinRep) return EconResult.Fail("rep_too_low");
        if (!_world.Actors.TryGet(actorId, out var actor) || !actor.IsAlive || (actor.Status & ActorFlags.Downed) != 0)
            return EconResult.Fail("unavailable");
        var txId = $"buy:{p.Id}:{nonce}";
        if (Ledger.WasApplied(txId)) return EconResult.Success; // retry of a completed purchase
        var reason = Items?.CanReceive(actorId, item);
        if (reason != null) return EconResult.Fail(reason);
        var from = currency == Currency.Dirty ? AccountId.Wallet(p.Id) : AccountId.Bank(p.Id);
        var price = PriceFor(actorId, item);
        var r = Ledger.Post(new Transfer(txId, from, AccountId.Sink("shop"), price, currency, $"buy:{item.Id}"));
        if (r.Status == TxStatus.InsufficientFunds) return EconResult.Fail("insufficient_funds");
        if (!r.Ok) return EconResult.Fail("invalid");
        Items?.Receive(actorId, item);
        if (item.Kind == ShopItemKind.Weapon) p.OwnedWeapons.Add(item.Ref);
        _world.Events.Publish(new PurchaseCompleted(actorId, item.Id, currency, price));
        return EconResult.Success;
    }

    /// <summary>Set by Activities (turf discount). Returns a price multiplier for this buyer, 1 = list price.</summary>
    public Func<int, ShopItem, double>? PriceModifier { get; set; }

    public long PriceFor(int actorId, ShopItem item)
    {
        var mul = System.Math.Clamp(PriceModifier?.Invoke(actorId, item) ?? 1.0, 0.0, 1.0);
        return (long)System.Math.Round(item.Price * mul);
    }

    /// <summary>Hands out a shop item for free (activity rewards). Returns false if the actor can't take it.</summary>
    public bool Grant(int actorId, string itemId)
    {
        if (!Data.Shop.TryGetValue(itemId, out var item) || Items == null || Items.CanReceive(actorId, item) != null) return false;
        Items.Receive(actorId, item);
        return true;
    }

    public EconResult BuyBusiness(int actorId, string businessId, ulong nonce)
    {
        if (!_byActor.TryGetValue(actorId, out var p)) return EconResult.Fail("no_profile");
        if (!Data.Businesses.TryGetValue(businessId, out var b)) return EconResult.Fail("unknown_business");
        if (_businessOwner.ContainsKey(businessId)) return EconResult.Fail("already_owned");
        var r = Ledger.Post(new Transfer($"biz:{p.Id}:{nonce}", AccountId.Bank(p.Id), AccountId.Sink("property"), b.Price, Currency.Clean, $"business:{b.Id}"));
        if (r.Status == TxStatus.InsufficientFunds) return EconResult.Fail("insufficient_funds");
        if (r.Status == TxStatus.Applied) _businessOwner[businessId] = p.Id;
        return r.Ok ? EconResult.Success : EconResult.Fail("invalid");
    }

    public string? OwnerOf(string businessId) => _businessOwner.GetValueOrDefault(businessId);

    // ------------------------------------------------------------------ laundering

    /// <summary>Instant laundering through a fixer: high fee, immediate clean money.</summary>
    public EconResult LaunderInstant(int actorId, long amount, ulong nonce)
        => Convert(actorId, AccountId.Wallet(ProfileIdOf(actorId)), amount, Data.Config.InstantLaunderFee, $"launder_i:{ProfileIdOf(actorId)}:{nonce}");

    /// <summary>Queues dirty money in a business the player's crew (or the player) owns; it converts over time at the business rate.</summary>
    public EconResult LaunderAtBusiness(int actorId, string businessId, long amount, ulong nonce)
    {
        if (!_byActor.TryGetValue(actorId, out var p)) return EconResult.Fail("no_profile");
        if (!Data.Businesses.ContainsKey(businessId)) return EconResult.Fail("unknown_business");
        if (!CanUseBusiness(p, businessId)) return EconResult.Fail("not_owner");
        var r = Ledger.Post(new Transfer($"launder_q:{p.Id}:{nonce}", AccountId.Wallet(p.Id), AccountId.Business(businessId), amount, Currency.Dirty, "launder_queue"));
        if (r.Status == TxStatus.InsufficientFunds) return EconResult.Fail("insufficient_funds");
        if (r.Status == TxStatus.Applied)
        {
            if (!_launderQueues.TryGetValue(businessId, out var q)) _launderQueues[businessId] = q = new Queue<LaunderJob>();
            q.Enqueue(new LaunderJob(p.Id, amount));
        }
        return r.Ok ? EconResult.Success : EconResult.Fail("invalid");
    }

    private bool CanUseBusiness(PlayerProfile p, string businessId)
    {
        if (!_businessOwner.TryGetValue(businessId, out var owner)) return false;
        if (owner == p.Id) return true;
        return p.CrewId != 0 && _profiles.TryGetValue(owner, out var o) && o.CrewId == p.CrewId;
    }

    private string ProfileIdOf(int actorId) => _byActor.TryGetValue(actorId, out var p) ? p.Id : "";

    private EconResult Convert(int actorId, AccountId from, long amount, double feeRate, string txId)
    {
        if (!_byActor.TryGetValue(actorId, out var p)) return EconResult.Fail("no_profile");
        if (amount <= 0) return EconResult.Fail("invalid");
        var batch = ConversionBatch(from, p.Id, amount, feeRate);
        var r = Ledger.PostBatch(txId, batch);
        if (r.Status == TxStatus.InsufficientFunds) return EconResult.Fail("insufficient_funds");
        return r.Ok ? EconResult.Success : EconResult.Fail("invalid");
    }

    /// <summary>dirty(from) → fee sink + exchange sink; exchange faucet → clean bank. Conservation holds per currency.</summary>
    private static List<Transfer> ConversionBatch(AccountId from, string profile, long amount, double feeRate)
        => ConversionBatchWithFee(from, profile, amount, (long)System.Math.Round(amount * feeRate));

    private static List<Transfer> ConversionBatchWithFee(AccountId from, string profile, long amount, long fee)
    {
        var net = amount - fee;
        var list = new List<Transfer>();
        if (fee > 0) list.Add(new Transfer("", from, AccountId.Sink("launder_fee"), fee, Currency.Dirty, "launder_fee"));
        if (net > 0)
        {
            list.Add(new Transfer("", from, AccountId.Sink("launder_exchange"), net, Currency.Dirty, "launder"));
            list.Add(new Transfer("", AccountId.Faucet("launder_exchange"), AccountId.Bank(profile), net, Currency.Clean, "launder"));
        }
        return list;
    }

    private void StepLaundering(double dt)
    {
        foreach (var (bizId, q) in _launderQueues)
        {
            if (q.Count == 0) continue;
            var biz = Data.Businesses[bizId];
            var carry = _launderCarry.GetValueOrDefault(bizId) + biz.ThroughputPerHour / 3600.0 * dt;
            var budget = (long)carry;
            _launderCarry[bizId] = carry - budget;
            while (budget > 0 && q.Count > 0)
            {
                var job = q.Peek();
                var chunk = System.Math.Min(budget, job.Remaining);
                // Fee is computed on the cumulative amount so per-tick rounding never leaks value.
                var done = job.Total - job.Remaining + chunk;
                var fee = (long)System.Math.Round(done * biz.FeeRate) - job.FeeCharged;
                var batch = ConversionBatchWithFee(AccountId.Business(bizId), job.Profile, chunk, fee);
                if (batch.Count > 0 && Ledger.PostBatch(NextTx($"launder_b:{bizId}"), batch).Ok)
                {
                    budget -= chunk;
                    job.Remaining -= chunk;
                    job.FeeCharged += fee;
                    var net = chunk - fee;
                    _world.Events.Publish(new LaunderingProgress(bizId, job.Profile, net, job.Remaining));
                }
                else break;
                if (job.Remaining <= 0) q.Dequeue();
            }
            if (q.Count == 0) _launderCarry[bizId] = 0;
        }
    }

    // ------------------------------------------------------------------ stash & crew vault

    public SafehouseDef? SafehouseAt(PlayerProfile p, Vec3d pos)
        => Data.Safehouses.FirstOrDefault(s => s.Contains(pos) && p.OwnedSafehouses.Contains(s.Id));

    public EconResult StashDeposit(int actorId, long amount, ulong nonce) => StashMove(actorId, amount, nonce, deposit: true);
    public EconResult StashWithdraw(int actorId, long amount, ulong nonce) => StashMove(actorId, amount, nonce, deposit: false);

    private EconResult StashMove(int actorId, long amount, ulong nonce, bool deposit)
    {
        if (!_byActor.TryGetValue(actorId, out var p) || !_world.Actors.TryGet(actorId, out var a)) return EconResult.Fail("no_profile");
        if (SafehouseAt(p, a.Position) == null) return EconResult.Fail("not_in_safehouse");
        var (from, to) = deposit ? (AccountId.Wallet(p.Id), AccountId.Stash(p.Id)) : (AccountId.Stash(p.Id), AccountId.Wallet(p.Id));
        var r = Ledger.Post(new Transfer($"stash:{p.Id}:{nonce}", from, to, amount, Currency.Dirty, deposit ? "stash_in" : "stash_out"));
        return r.Ok ? EconResult.Success : EconResult.Fail(r.Status == TxStatus.InsufficientFunds ? "insufficient_funds" : "invalid");
    }

    public EconResult CrewDeposit(int actorId, Currency c, long amount, ulong nonce)
    {
        if (!_byActor.TryGetValue(actorId, out var p) || p.CrewId == 0) return EconResult.Fail("no_crew");
        var from = c == Currency.Dirty ? AccountId.Wallet(p.Id) : AccountId.Bank(p.Id);
        var r = Ledger.Post(new Transfer($"crew_in:{p.Id}:{nonce}", from, AccountId.Crew(p.CrewId), amount, c, "crew_deposit"));
        return r.Ok ? EconResult.Success : EconResult.Fail(r.Status == TxStatus.InsufficientFunds ? "insufficient_funds" : "invalid");
    }

    /// <summary>Only the crew leader can pay out of the vault, and only to crew members (no free P2P transfers, §5.5).</summary>
    public EconResult CrewWithdraw(int actorId, string toProfile, Currency c, long amount, ulong nonce)
    {
        if (!_byActor.TryGetValue(actorId, out var p) || p.CrewId == 0) return EconResult.Fail("no_crew");
        var crew = Crews.Get(p.CrewId)!;
        if (crew.Leader != p.Id) return EconResult.Fail("not_leader");
        if (!crew.Members.Contains(toProfile)) return EconResult.Fail("not_member");
        var to = c == Currency.Dirty ? AccountId.Wallet(toProfile) : AccountId.Bank(toProfile);
        var r = Ledger.Post(new Transfer($"crew_out:{p.Id}:{nonce}", AccountId.Crew(p.CrewId), to, amount, c, "crew_withdraw"));
        return r.Ok ? EconResult.Success : EconResult.Fail(r.Status == TxStatus.InsufficientFunds ? "insufficient_funds" : "invalid");
    }

    // ------------------------------------------------------------------ penalties

    /// <summary>Takes a fraction of wallet cash into a sink (arrest fine, logout tax). Returns the amount taken.</summary>
    public long Penalize(string profileId, double fraction, string reason)
    {
        var amount = (long)System.Math.Floor(Wallet(profileId) * fraction);
        if (amount <= 0) return 0;
        return Ledger.Post(new Transfer(NextTx($"pen:{reason}:{profileId}"), AccountId.Wallet(profileId), AccountId.Sink(reason), amount, Currency.Dirty, reason)).Ok ? amount : 0;
    }

    // ------------------------------------------------------------------ death drops

    /// <summary>Drops part of the victim's dirty wallet as a cash bag. When a player made the kill the bag lands
    /// at the killer and the killer's crew has priority (PvP incentive); otherwise the victim's crew can recover it first.</summary>
    public int? DropOnDeath(int victimActorId, int killerActorId)
    {
        if (!_byActor.TryGetValue(victimActorId, out var victim) || !_world.Actors.TryGet(victimActorId, out var va)) return null;
        var cfg = Data.Config.DeathDrop;
        var wallet = Wallet(victim.Id);
        if (wallet <= 0) return null;
        var amount = System.Math.Min(wallet, System.Math.Clamp((long)System.Math.Round(wallet * cfg.Fraction), cfg.Min, cfg.Max));
        var killerProfile = _byActor.GetValueOrDefault(killerActorId);
        var pos = va.Position;
        if (killerProfile != null && _world.Actors.TryGet(killerActorId, out var ka)) pos = ka.Position;
        var dropId = _nextDrop++;
        if (!Ledger.Post(new Transfer($"drop:{dropId}", AccountId.Wallet(victim.Id), AccountId.Drop(dropId), amount, Currency.Dirty, "death_drop")).Ok)
            return null;
        var loot = _world.Actors.Spawn(ActorKind.Loot, pos + new Vec3d(0.6, 0, 0));
        loot.Name = "prop_cash_bag";
        var priority = killerProfile ?? victim;
        _drops[dropId] = new CashDrop(dropId, loot.Id, loot.Position, Now + cfg.LifetimeSeconds, Now + cfg.CrewPrioritySeconds,
            priority.CrewId, priority.Id);
        _world.Events.Publish(new CashDropped(dropId, loot.Id, amount, loot.Position));
        return dropId;
    }

    private void StepDrops()
    {
        var r = Data.Config.DeathDrop.PickupRadius;
        foreach (var d in _drops.Values.ToList())
        {
            var amount = Ledger.Balance(AccountId.Drop(d.Id), Currency.Dirty);
            if (Now >= d.ExpiresAt || amount <= 0)
            {
                if (amount > 0) Ledger.Post(new Transfer($"drop_expire:{d.Id}", AccountId.Drop(d.Id), AccountId.Sink("expired_drop"), amount, Currency.Dirty, "expired_drop"));
                RemoveDrop(d);
                continue;
            }
            var taker = _byActor
                .Where(kv => _world.Actors.TryGet(kv.Key, out var a) && a.IsAlive && (a.Status & ActorFlags.Downed) == 0
                             && (a.Position - d.Position).WithY(0).Length <= r
                             && (Now >= d.PriorityUntil || kv.Value.Id == d.PriorityProfile || (d.PriorityCrew != 0 && kv.Value.CrewId == d.PriorityCrew)))
                .OrderBy(kv => kv.Key).Select(kv => (int?)kv.Key).FirstOrDefault();
            if (taker is not { } actorId) continue;
            var p = _byActor[actorId];
            if (Ledger.Post(new Transfer($"pickup:{d.Id}", AccountId.Drop(d.Id), AccountId.Wallet(p.Id), amount, Currency.Dirty, "pickup")).Ok)
            {
                _world.Events.Publish(new CashPickedUp(d.Id, actorId, amount));
                RemoveDrop(d);
            }
        }
    }

    private void RemoveDrop(CashDrop d)
    {
        _drops.Remove(d.Id);
        _world.Actors.Remove(d.LootActorId);
    }

    public int ActiveDrops => _drops.Count;

    // ------------------------------------------------------------------ tick

    public void Step(double dt)
    {
        Now += dt;
        StepLaundering(dt);
        StepDrops();
    }

    private void OnPosted(Transfer t)
    {
        if (_world == null) return;
        foreach (var acc in new[] { t.From, t.To })
        {
            if (acc.Kind is not (AccountKind.Wallet or AccountKind.Bank or AccountKind.Stash)) continue;
            var delta = acc == t.To ? t.Amount : -t.Amount;
            _world.Events.Publish(new BalanceChanged(acc.Key, acc, t.Currency, Ledger.Balance(acc, t.Currency), delta, t.Reason));
        }
    }
}
