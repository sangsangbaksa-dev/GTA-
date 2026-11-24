using Harbor.Core.Economy;

namespace Harbor.Core.Activities;

/// <summary>Bounties (§6.5, §6.6): a player who stays at wanted level ≥ N for long enough — or keeps killing the same
/// player — gets a bounty. Part of their dirty wallet is seized into escrow; the hunter who kills them is paid in clean
/// money (base + seized amount). Surviving until the wanted level drops returns the seized cash.</summary>
public sealed class Bounties(ActivitySystem sys)
{
    private readonly Dictionary<int, Bounty> _bounties = new();
    private readonly Dictionary<int, double> _wantedFor = new();
    private int _next = 1;

    public sealed record Bounty(int Id, int TargetActorId, string TargetProfile, long Base, long Seized, string Reason)
    {
        public long Amount => Base + Seized;
        public AccountId Escrow => AccountId.Escrow($"bounty:{Id}");
    }

    private EconomyConfig.BountyDef Cfg => sys.Economy.Data.Config.Bounty;

    public Bounty? On(int actorId) => _bounties.GetValueOrDefault(actorId);

    public void Step(double dt)
    {
        foreach (var (a, _) in sys.Players().ToList())
        {
            var level = sys.Heat.LevelOf(a.Id);
            if (level >= Cfg.MinHeatLevel) _wantedFor[a.Id] = _wantedFor.GetValueOrDefault(a.Id) + dt;
            else _wantedFor.Remove(a.Id);
            if (!_bounties.ContainsKey(a.Id) && _wantedFor.GetValueOrDefault(a.Id) >= Cfg.HoldSeconds) Place(a.Id, "wanted");
            if (_bounties.TryGetValue(a.Id, out var b) && level == 0) Clear(b, "evaded", returnToTarget: true);
        }
    }

    public Bounty? Place(int actorId, string reason)
    {
        if (_bounties.TryGetValue(actorId, out var existing)) return existing;
        if (sys.Economy.ProfileOf(actorId) is not { } p) return null;
        var id = _next++;
        var seize = (long)System.Math.Floor(sys.Economy.Wallet(p.Id) * Cfg.WalletFraction);
        var b = new Bounty(id, actorId, p.Id, Cfg.Base, 0, reason);
        if (seize > 0 && sys.Economy.Move($"bounty_seize:{id}", AccountId.Wallet(p.Id), b.Escrow, seize, Currency.Dirty, "bounty_seize").Ok)
            b = b with { Seized = seize };
        _bounties[actorId] = b;
        sys.World.Events.Publish(new BountyPlaced(actorId, p.Id, b.Amount, reason));
        return b;
    }

    public void OnKilled(int victimId, int killerId)
    {
        if (!_bounties.TryGetValue(victimId, out var b)) return;
        var hunter = killerId == victimId ? null : sys.Economy.ProfileOf(killerId);
        var victim = sys.Economy.Profile(b.TargetProfile);
        var sameCrew = hunter != null && victim != null && hunter.CrewId != 0 && hunter.CrewId == victim.CrewId;
        if (hunter == null || sameCrew) { Clear(b, "died", returnToTarget: false); return; }

        // seized dirty cash is destroyed; the hunter is paid the same value in clean money (police reward)
        var batch = new List<Transfer>();
        if (b.Seized > 0) batch.Add(new Transfer("", b.Escrow, AccountId.Sink("bounty_seized"), b.Seized, Currency.Dirty, "bounty"));
        batch.Add(new Transfer("", AccountId.Faucet("bounty"), AccountId.Bank(hunter.Id), b.Amount, Currency.Clean, "bounty"));
        sys.Economy.Ledger.PostBatch($"bounty_claim:{b.Id}", batch);
        _bounties.Remove(victimId);
        _wantedFor.Remove(victimId);
        sys.World.Events.Publish(new BountyClaimed(victimId, killerId, b.Amount));
    }

    public void OnLeft(int actorId)
    {
        _wantedFor.Remove(actorId);
        if (_bounties.TryGetValue(actorId, out var b)) Clear(b, "left", returnToTarget: false);
    }

    private void Clear(Bounty b, string reason, bool returnToTarget)
    {
        _bounties.Remove(b.TargetActorId);
        if (b.Seized > 0)
        {
            var to = returnToTarget ? AccountId.Wallet(b.TargetProfile) : AccountId.Sink("bounty_forfeit");
            sys.Economy.Move($"bounty_clear:{b.Id}", b.Escrow, to, b.Seized, Currency.Dirty, $"bounty_{reason}");
        }
        sys.World.Events.Publish(new BountyCleared(b.TargetActorId, reason));
    }
}
