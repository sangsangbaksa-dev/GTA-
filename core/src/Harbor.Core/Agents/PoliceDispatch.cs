using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Sim;

namespace Harbor.Core.Agents;

/// <summary>Sends police units after wanted players (CONTENT_DESIGN §3.2). Unit count and response delay come
/// from the Heat level table; the server composition calls <see cref="SetWanted"/> when a level changes.
/// Units spawn out of sight on a ring around the suspect, hunt them, and stand down once the level drops to 0.</summary>
public sealed class PoliceDispatch
{
    private readonly AgentSystem _agents;
    private readonly Dictionary<int, Case> _cases = new();

    private sealed class Case
    {
        public int Level;
        public int Units;
        public double ResponseSeconds;
        public double NextSpawnAt;
        public double ClearedAt = double.NegativeInfinity;
        public readonly List<int> Officers = new();
    }

    internal PoliceDispatch(AgentSystem agents) => _agents = agents;

    public int UnitsFor(int playerId) => _cases.TryGetValue(playerId, out var c) ? c.Officers.Count : 0;
    public int TotalUnits => _cases.Values.Sum(c => c.Officers.Count);

    /// <summary>Wanted level of <paramref name="playerId"/> changed to <paramref name="level"/> (units/response from heat.json).</summary>
    public void SetWanted(int playerId, int level, int units, double responseSeconds)
    {
        if (!_cases.TryGetValue(playerId, out var c))
        {
            if (level <= 0) return;
            _cases[playerId] = c = new Case { NextSpawnAt = _agents.Now + responseSeconds };
        }
        if (level > 0 && c.Level == 0) c.NextSpawnAt = _agents.Now + responseSeconds;
        c.Level = level;
        c.Units = System.Math.Min(units, _agents.Data.Police.MaxUnitsPerPlayer);
        c.ResponseSeconds = responseSeconds;
        c.ClearedAt = level <= 0 ? _agents.Now : double.NegativeInfinity;
    }

    internal void Forget(int actorId)
    {
        foreach (var c in _cases.Values) c.Officers.Remove(actorId);
    }

    internal void Step(double dt)
    {
        var cfg = _agents.Data.Police;
        var now = _agents.Now;
        foreach (var (playerId, c) in _cases.ToList())
        {
            c.Officers.RemoveAll(id => !_agents.IsAgent(id));
            var suspect = _agents.World.Actors.TryGet(playerId, out var p) && p.IsAlive ? p : null;
            if (suspect == null || c.Level <= 0)
            {
                if (c.ClearedAt < 0) c.ClearedAt = now;
                if (now - c.ClearedAt >= cfg.DespawnAfterSeconds || suspect == null && !_agents.World.Actors.TryGet(playerId, out _))
                {
                    foreach (var id in c.Officers.ToList())
                        if (_agents.Get(id) is { } ag && (ag.Target < 0 || !ag.Actor.IsAlive)) _agents.Despawn(id);
                    if (c.Officers.Count == 0) _cases.Remove(playerId);
                }
                continue;
            }
            var alive = c.Officers.Count(id => _agents.Get(id)?.Actor.IsAlive == true);
            if (alive >= c.Units || now < c.NextSpawnAt || _agents.Data.Archetypes.Count == 0) continue;
            if (TotalUnits >= cfg.MaxUnitsTotal) continue;
            var batch = System.Math.Min(cfg.SpawnBatch, c.Units - alive);
            var spot = SpawnSpot(suspect.Position);
            for (var i = 0; i < batch; i++)
            {
                var arch = c.Level >= cfg.SwatFromLevel && i % 2 == 1 ? cfg.Swat : cfg.Officer;
                var ag = _agents.Spawn(arch, _agents.FreeSpotNear(spot, 3, i), null, leash: double.MaxValue);
                ag.AssignedTo = playerId;
                ag.Alert = suspect.Position;
                ag.AlertUntil = now + 5;
                c.Officers.Add(ag.Actor.Id);
            }
            // reinforcements arrive in waves, faster at higher levels
            c.NextSpawnAt = now + System.Math.Max(3, c.ResponseSeconds * 0.5);
        }
    }

    private Vec3d SpawnSpot(Vec3d around)
    {
        var cfg = _agents.Data.Police;
        var rng = _agents.Rng;
        Vec3d best = around;
        for (var tries = 0; tries < 10; tries++)
        {
            var ang = rng.NextDouble() * 2 * System.Math.PI;
            var r = cfg.SpawnDistance[0] + rng.NextDouble() * (cfg.SpawnDistance[1] - cfg.SpawnDistance[0]);
            var p = around + new Vec3d(System.Math.Cos(ang) * r, 0, System.Math.Sin(ang) * r);
            best = p;
            // prefer a spot the suspect cannot see (units "arrive" rather than pop in view)
            var eye = around + new Vec3d(0, 1.6, 0);
            var d = p + new Vec3d(0, 1.2, 0) - eye;
            if (_agents.StaticWorld.Raycast(eye, d, d.Length, out RayHit _)) return p;
        }
        return best;
    }
}
