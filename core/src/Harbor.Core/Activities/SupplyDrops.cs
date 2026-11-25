using Harbor.Core.Economy;
using Harbor.Core.Math;
using Harbor.Core.Missions;
using Harbor.Core.Sim;

namespace Harbor.Core.Activities;

/// <summary>Supply drops (§6.4): a public crate lands on a schedule, announced in advance. Opening it takes a held
/// interact; getting hit or stepping away cancels that player's progress. First to finish takes the cash and kit.</summary>
public sealed class SupplyDrops
{
    private readonly ActivitySystem _sys;
    private readonly Dictionary<int, Drop> _active = new();
    private Pending? _pending;
    private double _nextAt;
    private int _next = 1;

    private SupplyDropConfig Cfg => _sys.Data.SupplyDrops;
    private sealed record Pending(int Id, SupplySiteDef Site, double LandsAt);

    public sealed class Drop
    {
        public int Id { get; init; }
        public string SiteId { get; init; } = "";
        public int CrateActorId { get; init; }
        public Vec3d Position { get; init; }
        public double ExpiresAt { get; init; }
        internal readonly Dictionary<int, double> Progress = new();
        public double ProgressOf(int actorId) => Progress.GetValueOrDefault(actorId);
    }

    public SupplyDrops(ActivitySystem sys)
    {
        _sys = sys;
        _nextAt = sys.Data.SupplyDrops.FirstDropSeconds;
    }

    public IEnumerable<Drop> Active => _active.Values;
    public double NextLandingAt => _pending?.LandsAt ?? _nextAt;

    public void Step(double dt)
    {
        var now = _sys.Now;
        if (Cfg.Sites.Count == 0) return;
        if (_pending == null && now >= _nextAt - Cfg.NoticeSeconds)
        {
            var id = _next++;
            var site = Cfg.Sites[(id * 7 + 3) % Cfg.Sites.Count]; // fixed rotation, no back-to-back repeats when sites >= 2
            _pending = new Pending(id, site, _nextAt);
            _sys.World.Events.Publish(new SupplyDropAnnounced(id, site.Id, site.At.ToVec(), System.Math.Max(0, _nextAt - now)));
        }
        if (_pending != null && now >= _pending.LandsAt)
        {
            var pos = _pending.Site.At.ToVec();
            var crate = _sys.World.Actors.Spawn(ActorKind.Pickup, pos);
            crate.Name = "prop_weapon_crate";
            var drop = new Drop { Id = _pending.Id, SiteId = _pending.Site.Id, CrateActorId = crate.Id, Position = pos, ExpiresAt = now + Cfg.LifetimeSeconds };
            _active[drop.Id] = drop;
            _sys.World.Events.Publish(new SupplyDropLanded(drop.Id, crate.Id, pos));
            _pending = null;
            _nextAt += Cfg.IntervalSeconds;
        }

        foreach (var d in _active.Values.ToList())
        {
            if (now >= d.ExpiresAt) { Remove(d); _sys.World.Events.Publish(new SupplyDropExpired(d.Id)); continue; }
            var openers = _sys.Players()
                .Where(x => x.Actor.IsActive() && (x.Actor.Status & ActorFlags.Carrying) == 0 && _sys.Input.IsHeld(x.Actor.Id)
                            && x.Actor.Vitals.SecondsSinceDamage >= _sys.Missions.InterruptSeconds
                            && ActorQueries.FlatDistance(x.Actor.Position, d.Position) <= Cfg.OpenRadius)
                .Select(x => x.Actor.Id).ToHashSet();
            foreach (var id in d.Progress.Keys.Where(id => !openers.Contains(id)).ToList()) d.Progress.Remove(id);
            foreach (var id in openers) d.Progress[id] = d.Progress.GetValueOrDefault(id) + dt;
            var winner = d.Progress.Where(kv => kv.Value >= Cfg.OpenSeconds - 1e-9)
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => (int?)kv.Key).FirstOrDefault();
            if (winner is { } w) Open(d, w);
        }
    }

    private void Open(Drop d, int actorId)
    {
        Remove(d);
        var p = _sys.Economy.ProfileOf(actorId)!;
        var cash = _sys.Economy.Payout(p.Id, Currency.Dirty, Cfg.Cash, "supply_drop", $"supply:{d.Id}");
        foreach (var item in Cfg.Items) _sys.Economy.Grant(actorId, item);
        _sys.World.Events.Publish(new SupplyDropOpened(d.Id, actorId, cash));
    }

    private void Remove(Drop d)
    {
        _active.Remove(d.Id);
        _sys.World.Actors.Remove(d.CrateActorId);
    }
}
