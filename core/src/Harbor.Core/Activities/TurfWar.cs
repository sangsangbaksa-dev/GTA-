using Harbor.Core.Economy;
using Harbor.Core.Missions;

namespace Harbor.Core.Activities;

/// <summary>Turf (§6.3): stand uncontested in a zone to capture it; owners earn hourly income into the crew vault and
/// get a shop discount in that district. Owned zones open to attack on a schedule, announced in advance, and only
/// while at least one owner is online (offline protection — income pauses too).</summary>
public sealed class TurfWar
{
    private readonly ActivitySystem _sys;
    private readonly Dictionary<string, Zone> _zones;
    private TurfConfig Cfg => _sys.Data.Turf;

    public sealed class Zone(TurfZoneDef def)
    {
        public TurfZoneDef Def { get; } = def;
        public int Owner { get; internal set; }
        public int CapturingCrew { get; internal set; }
        public double CaptureProgress { get; internal set; }
        public bool AttackOpen => WindowEndsAt > 0;
        internal double NextAttackAt;
        internal bool Noticed;
        internal double WindowEndsAt;
        internal double IncomeCarry;
        internal long Pending;
        internal double NextPayoutAt;
        internal int Seq;
    }

    public TurfWar(ActivitySystem sys)
    {
        _sys = sys;
        _zones = sys.Data.Turf.Zones.ToDictionary(z => z.Id, z => new Zone(z));
    }

    public Zone? Get(string id) => _zones.GetValueOrDefault(id);
    public IEnumerable<Zone> Zones => _zones.Values;
    public int OwnedBy(int crewId) => _zones.Values.Count(z => z.Owner == crewId && crewId != 0);

    public void Step(double dt)
    {
        var now = _sys.Now;
        var players = _sys.Players().ToList();
        var online = players.Where(x => x.Profile.CrewId != 0).GroupBy(x => x.Profile.CrewId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var z in _zones.Values)
        {
            if (z.Owner != 0) StepOwned(z, now, dt, online.GetValueOrDefault(z.Owner));
            var capturable = z.Owner == 0 || z.AttackOpen;
            if (!capturable) { z.CaptureProgress = 0; z.CapturingCrew = 0; continue; }

            var present = players
                .Where(x => x.Profile.CrewId != 0 && x.Actor.IsActive() && ActorQueries.FlatDistance(x.Actor.Position, z.Def.Center) <= z.Def.Radius)
                .Select(x => x.Profile.CrewId).ToHashSet();
            var attackers = present.Where(c => c != z.Owner).ToList();
            if (attackers.Count == 1 && !present.Contains(z.Owner))
            {
                var c = attackers[0];
                if (z.CapturingCrew != c) { z.CapturingCrew = c; z.CaptureProgress = 0; }
                z.CaptureProgress += dt;
                if (z.CaptureProgress >= Cfg.CaptureSeconds - 1e-9) Capture(z, c, now);
            }
            else if (attackers.Count == 0)
            {
                z.CaptureProgress = System.Math.Max(0, z.CaptureProgress - dt);
                if (z.CaptureProgress == 0) z.CapturingCrew = 0;
            }
            // else contested: progress holds
        }
    }

    private void StepOwned(Zone z, double now, double dt, int ownersOnline)
    {
        if (ownersOnline == 0 && !z.AttackOpen)
        {
            z.NextAttackAt += dt; // offline protection: the clock stops
            z.NextPayoutAt += dt;
            return;
        }
        z.IncomeCarry += z.Def.IncomePerHour / 3600.0 * dt;
        var whole = (long)z.IncomeCarry;
        z.IncomeCarry -= whole;
        z.Pending += whole;
        if (now >= z.NextPayoutAt) { Flush(z); z.NextPayoutAt = now + Cfg.PayoutIntervalSeconds; }

        if (!z.AttackOpen)
        {
            if (!z.Noticed && now >= z.NextAttackAt - Cfg.AttackNoticeSeconds)
            {
                z.Noticed = true;
                _sys.World.Events.Publish(new TurfAttackScheduled(z.Def.Id, z.Owner, System.Math.Max(0, z.NextAttackAt - now)));
            }
            if (now >= z.NextAttackAt)
            {
                z.WindowEndsAt = now + Cfg.AttackWindowSeconds;
                _sys.World.Events.Publish(new TurfAttackStarted(z.Def.Id, z.Owner, Cfg.AttackWindowSeconds));
            }
        }
        else if (now >= z.WindowEndsAt)
        {
            ResetSchedule(z, now);
            _sys.World.Events.Publish(new TurfDefended(z.Def.Id, z.Owner));
        }
    }

    private void Capture(Zone z, int crew, double now)
    {
        Flush(z);
        var old = z.Owner;
        z.Owner = crew;
        z.IncomeCarry = 0;
        ResetSchedule(z, now);
        z.NextPayoutAt = now + Cfg.PayoutIntervalSeconds;
        if (_sys.Economy.Crews.Get(crew) is { } c) c.Rep += 1;
        _sys.World.Events.Publish(new TurfCaptured(z.Def.Id, crew, old));
    }

    private void ResetSchedule(Zone z, double now)
    {
        z.WindowEndsAt = 0;
        z.Noticed = false;
        z.NextAttackAt = now + Cfg.AttackIntervalSeconds;
        z.CaptureProgress = 0;
        z.CapturingCrew = 0;
    }

    private void Flush(Zone z)
    {
        if (z.Pending <= 0 || z.Owner == 0) { z.Pending = 0; return; }
        var amount = z.Pending;
        z.Pending = 0;
        if (_sys.Economy.Move($"turf:{z.Def.Id}:{++z.Seq}", AccountId.Faucet("turf"), AccountId.Crew(z.Owner), amount, Currency.Dirty, "turf").Ok)
            _sys.World.Events.Publish(new TurfIncome(z.Def.Id, z.Owner, amount));
    }

    /// <summary>Shop price multiplier: discounted when the buyer's crew owns a zone in the district they stand in
    /// (district = that of the nearest zone).</summary>
    public double PriceMultiplier(int actorId, ShopItem item)
    {
        if (_zones.Count == 0 || _sys.Economy.ProfileOf(actorId) is not { CrewId: not 0 } p
            || !_sys.World.Actors.TryGet(actorId, out var a)) return 1.0;
        var district = _zones.Values.OrderBy(z => ActorQueries.FlatDistance(z.Def.Center, a.Position)).First().Def.District;
        return _zones.Values.Any(z => z.Owner == p.CrewId && z.Def.District == district) ? 1.0 - Cfg.ShopDiscount : 1.0;
    }
}
