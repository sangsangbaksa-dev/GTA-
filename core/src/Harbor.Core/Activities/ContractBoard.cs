using Harbor.Core.Economy;
using Harbor.Core.Missions;
using Harbor.Core.Sim;

namespace Harbor.Core.Activities;

/// <summary>Contracts (§6.1) and heists (§6.2). A heist is a set of prep contracts plus a finale contract whose
/// delivered cash bags go into escrow; the crew leader then splits the take.</summary>
public sealed class ContractBoard
{
    private readonly ActivitySystem _sys;
    private readonly Dictionary<int, RunInfo> _runs = new();
    private readonly Dictionary<(string Profile, string Contract), double> _cooldownUntil = new();
    private readonly Dictionary<int, HashSet<string>> _crewPreps = new();
    private readonly Dictionary<int, PendingSplit> _splits = new();
    private readonly HashSet<(string Profile, ulong Nonce)> _startNonces = new();

    private sealed record RunInfo(ContractDef Def, HeistDef? Heist, int CrewId, MissionRun Run);
    private sealed record PendingSplit(int RunId, HeistDef Heist, int CrewId, List<string> Members, double Deadline);

    public ContractBoard(ActivitySystem sys)
    {
        _sys = sys;
        sys.World.Events.Subscribe<MissionEnded>(OnMissionEnded);
        sys.World.Events.Subscribe<ItemDelivered>(OnItemDelivered);
    }

    private EconomySystem Eco => _sys.Economy;

    public IReadOnlySet<string> PrepsDone(int crewId) =>
        _crewPreps.TryGetValue(crewId, out var s) ? s : new HashSet<string>();

    public bool HasPendingSplit(int runId) => _splits.ContainsKey(runId);
    public IReadOnlyList<string>? SplitMembers(int runId) => _splits.GetValueOrDefault(runId)?.Members;

    public static string EscrowKey(int runId) => $"heist:{runId}";

    // ------------------------------------------------------------------ start

    public EconResult Start(int actorId, string contractId, ulong nonce)
    {
        var p = Eco.ProfileOf(actorId);
        if (p == null || !_sys.World.Actors.TryGet(actorId, out var actor)) return EconResult.Fail("no_profile");
        if (_startNonces.Contains((p.Id, nonce))) return EconResult.Success; // resent request
        if (!_sys.Data.Contracts.TryGetValue(contractId, out var def)) return EconResult.Fail("unknown_contract");
        if ((actor.Status & ActorFlags.Passive) != 0) return EconResult.Fail("passive_mode");
        if (!actor.IsActive()) return EconResult.Fail("unavailable");
        if (_sys.Missions.RunOf(actorId) != null) return EconResult.Fail("already_in_activity");
        if (p.Rep < def.MinRep) return EconResult.Fail("rep_too_low");
        if (_cooldownUntil.TryGetValue((p.Id, def.Id), out var until) && _sys.Now < until) return EconResult.Fail("cooldown");

        var heist = _sys.Data.HeistOf(def.Id);
        var minPlayers = def.MinPlayers;
        if (heist != null)
        {
            if (p.CrewId == 0) return EconResult.Fail("crew_required");
            var crew = Eco.Crews.Get(p.CrewId);
            var done = PrepsDone(p.CrewId);
            if (_runs.Values.Any(r => r.CrewId == p.CrewId && r.Heist == heist)) return EconResult.Fail("heist_in_progress");
            if (def.Kind == ContractKind.HeistPrep && done.Contains(def.Id)) return EconResult.Fail("prep_done");
            if (def.Kind == ContractKind.HeistFinale)
            {
                if (crew?.Leader != p.Id) return EconResult.Fail("leader_only");
                if (!heist.Preps.All(done.Contains)) return EconResult.Fail("preps_incomplete");
                if (_splits.Values.Any(s => s.CrewId == p.CrewId)) return EconResult.Fail("split_pending");
                minPlayers = System.Math.Max(minPlayers, heist.MinPlayers);
            }
        }

        var party = new List<int> { actorId };
        if (p.CrewId != 0)
            party.AddRange(_sys.Players()
                .Where(x => x.Actor.Id != actorId && x.Profile.CrewId == p.CrewId && x.Actor.IsActive()
                            && _sys.Missions.RunOf(x.Actor.Id) == null
                            && ActorQueries.FlatDistance(x.Actor.Position, actor.Position) <= def.PartyRadius)
                .OrderBy(x => ActorQueries.FlatDistance(x.Actor.Position, actor.Position)).ThenBy(x => x.Actor.Id)
                .Take(def.MaxPlayers - 1).Select(x => x.Actor.Id));
        if (party.Count < minPlayers) return EconResult.Fail("not_enough_players");

        _startNonces.Add((p.Id, nonce));
        var run = _sys.Missions.Start($"contract:{def.Id}", def.Objectives, def.TimeLimitSeconds, party);
        _runs[run.Id] = new RunInfo(def, heist, p.CrewId, run);
        _sys.World.Events.Publish(new ContractStarted(run.Id, def.Id, party.ToArray()));
        return EconResult.Success;
    }

    public EconResult Abandon(int actorId)
    {
        if (_sys.Missions.RunOf(actorId) == null) return EconResult.Fail("not_in_activity");
        _sys.Carry.Drop(actorId);
        _sys.Missions.RemoveMember(actorId);
        return EconResult.Success;
    }

    // ------------------------------------------------------------------ results

    private void OnItemDelivered(ItemDelivered e)
    {
        if (!_runs.TryGetValue(e.RunId, out var info) || info.Def.Kind != ContractKind.HeistFinale || e.Value <= 0) return;
        Eco.Move($"bag:{e.RunId}:{e.ItemId}", AccountId.Faucet($"heist:{info.Heist!.Id}"), AccountId.Escrow(EscrowKey(e.RunId)),
            e.Value, Currency.Dirty, "heist_bag");
    }

    private void OnMissionEnded(MissionEnded e)
    {
        if (!_runs.Remove(e.RunId, out var info)) return;
        var def = info.Def;
        var members = info.Run.Party.Where(id => Eco.ProfileOf(id) != null).ToList();
        var profiles = members.Select(id => Eco.ProfileOf(id)!).ToList();
        foreach (var p in profiles) _cooldownUntil[(p.Id, def.Id)] = _sys.Now + def.CooldownSeconds;
        long paid = 0;

        if (e.Success && profiles.Count > 0)
        {
            foreach (var p in profiles) p.AddXp(def.Reward.Xp);
            if (def.Kind == ContractKind.HeistFinale) BeginSplit(info, profiles, members);
            else paid = PayEqual(e.RunId, def, profiles, info.Run.TimeLeftFraction);
            if (def.Kind == ContractKind.HeistPrep)
            {
                if (!_crewPreps.TryGetValue(info.CrewId, out var set)) _crewPreps[info.CrewId] = set = new HashSet<string>();
                set.Add(def.Id);
                _sys.World.Events.Publish(new PrepCompleted(info.CrewId, info.Heist!.Id, def.Id));
            }
        }
        else if (!e.Success && def.Kind == ContractKind.HeistFinale)
        {
            Forfeit(e.RunId, "heist_failed");
            LoseRandomPrep(info, e.RunId);
        }
        _sys.World.Events.Publish(new ContractEnded(e.RunId, def.Id, e.Success, e.Reason, members, paid));
    }

    /// <summary>Contract reward scales from min to max with time left, split evenly; diminishing returns apply per player.</summary>
    private long PayEqual(int runId, ContractDef def, List<PlayerProfile> profiles, double timeLeft)
    {
        var total = def.Reward.Min + (long)System.Math.Round((def.Reward.Max - def.Reward.Min) * timeLeft);
        total = total / 10 * 10;
        var share = total / profiles.Count;
        long paid = 0;
        foreach (var p in profiles)
            paid += Eco.Payout(p.Id, def.Reward.Currency, share, $"contract:{def.Id}", $"ct:{runId}:{p.Id}");
        return paid;
    }

    private void LoseRandomPrep(RunInfo info, int runId)
    {
        if (!_crewPreps.TryGetValue(info.CrewId, out var set)) return;
        var held = info.Heist!.Preps.Where(set.Contains).ToList();
        if (held.Count == 0) return;
        var lost = held[(int)((uint)(runId * 2654435761u) % (uint)held.Count)];
        set.Remove(lost);
        _sys.World.Events.Publish(new PrepLost(info.CrewId, info.Heist.Id, lost));
    }

    // ------------------------------------------------------------------ heist split

    private void BeginSplit(RunInfo info, List<PlayerProfile> profiles, List<int> actors)
    {
        var heist = info.Heist!;
        if (_crewPreps.TryGetValue(info.CrewId, out var set)) foreach (var prep in heist.Preps) set.Remove(prep);
        if (Eco.Crews.Get(info.CrewId) is { } crew) crew.Rep += heist.CrewRep;
        var take = Eco.Ledger.Balance(AccountId.Escrow(EscrowKey(info.Run.Id)), Currency.Dirty);
        var members = profiles.Select(p => p.Id).ToList();
        _splits[info.Run.Id] = new PendingSplit(info.Run.Id, heist, info.CrewId, members, _sys.Now + heist.SplitTimeoutSeconds);
        _sys.World.Events.Publish(new HeistSplitPending(info.Run.Id, heist.Id, members, actors, take, heist.SplitTimeoutSeconds));
    }

    /// <summary>Leader sets shares in percent, one per member in the order of <see cref="HeistSplitPending.Members"/>.</summary>
    public EconResult SetSplit(int actorId, int runId, IReadOnlyList<int> percents)
    {
        var p = Eco.ProfileOf(actorId);
        if (p == null) return EconResult.Fail("no_profile");
        if (!_splits.TryGetValue(runId, out var split)) return EconResult.Fail("no_split");
        if (Eco.Crews.Get(split.CrewId)?.Leader != p.Id) return EconResult.Fail("leader_only");
        if (percents.Count != split.Members.Count) return EconResult.Fail("invalid_split");
        if (percents.Any(x => x < split.Heist.MinSharePercent) || percents.Sum() != 100) return EconResult.Fail("invalid_split");
        Pay(split, percents);
        return EconResult.Success;
    }

    /// <summary>Packs up to four percentages into one integer (byte per member) for the wire.</summary>
    public static long PackPercents(IReadOnlyList<int> percents)
    {
        long v = 0;
        for (var i = 0; i < percents.Count && i < 4; i++) v |= (long)(percents[i] & 0xFF) << (8 * i);
        return v;
    }

    public static List<int> UnpackPercents(long packed, int count) =>
        Enumerable.Range(0, System.Math.Clamp(count, 0, 4)).Select(i => (int)((packed >> (8 * i)) & 0xFF)).ToList();

    public void Step()
    {
        foreach (var s in _splits.Values.Where(s => _sys.Now >= s.Deadline).ToList())
        {
            var n = s.Members.Count;
            var even = Enumerable.Repeat(100 / n, n).ToList();
            even[0] += 100 - even.Sum();
            Pay(s, even);
        }
    }

    private void Pay(PendingSplit split, IReadOnlyList<int> percents)
    {
        _splits.Remove(split.RunId);
        var escrow = AccountId.Escrow(EscrowKey(split.RunId));
        var take = Eco.Ledger.Balance(escrow, Currency.Dirty);
        var shares = new Dictionary<string, long>();
        for (var i = 0; i < split.Members.Count; i++) shares[split.Members[i]] = take * percents[i] / 100;
        // rounding leftovers go to the member with the largest share (first on ties) so escrow ends at exactly 0
        var top = split.Members.OrderByDescending(m => shares[m]).First();
        shares[top] += take - shares.Values.Sum();
        var batch = shares.Where(kv => kv.Value > 0)
            .Select(kv => new Transfer("", escrow, AccountId.Wallet(kv.Key), kv.Value, Currency.Dirty, "heist_split")).ToList();
        if (batch.Count > 0) Eco.Ledger.PostBatch($"split:{split.RunId}", batch);
        _sys.World.Events.Publish(new HeistPaid(split.RunId, split.Heist.Id, shares));
    }

    private void Forfeit(int runId, string reason)
    {
        var escrow = AccountId.Escrow(EscrowKey(runId));
        var amount = Eco.Ledger.Balance(escrow, Currency.Dirty);
        if (amount > 0) Eco.Move($"forfeit:{runId}", escrow, AccountId.Sink(reason), amount, Currency.Dirty, reason);
    }
}
