using Harbor.Core.Math;

namespace Harbor.Core.Net;

/// <summary>Client-side buffer of received states for remote actors, sampled slightly in the past
/// (render tick = latest server tick − delay) so motion is smooth despite 20 Hz snapshots and jitter.</summary>
public sealed class SnapshotInterpolator(int maxSamples = 16)
{
    private readonly Dictionary<int, List<(double Tick, EntityState State)>> _tracks = new();

    public IEnumerable<int> Ids => _tracks.Keys;

    public void Add(uint serverTick, IEnumerable<EntityState> states, int skipId = -1)
    {
        foreach (var s in states)
        {
            if (s.Id == skipId) continue;
            if (!_tracks.TryGetValue(s.Id, out var t)) _tracks[s.Id] = t = new List<(double, EntityState)>();
            if (t.Count > 0 && t[^1].Tick >= serverTick) continue; // out of order / duplicate
            t.Add((serverTick, s));
            if (t.Count > maxSamples) t.RemoveAt(0);
        }
    }

    /// <summary>Forget actors not present in snapshots since <paramref name="olderThanTick"/>.</summary>
    public void Prune(double olderThanTick)
    {
        foreach (var id in _tracks.Where(kv => kv.Value[^1].Tick < olderThanTick).Select(kv => kv.Key).ToList())
            _tracks.Remove(id);
    }

    public bool TrySample(int id, double renderTick, out EntityState state)
    {
        state = default;
        if (!_tracks.TryGetValue(id, out var t) || t.Count == 0) return false;
        if (renderTick <= t[0].Tick) { state = t[0].State; return true; }
        for (var i = 0; i < t.Count - 1; i++)
        {
            var (ta, a) = t[i];
            var (tb, b) = t[i + 1];
            if (renderTick < ta || renderTick > tb) continue;
            var k = (renderTick - ta) / (tb - ta);
            state = a with
            {
                Position = Vec3d.Lerp(a.Position, b.Position, k),
                Velocity = Vec3d.Lerp(a.Velocity, b.Velocity, k),
                Yaw = LerpAngle(a.Yaw, b.Yaw, k),
                Pitch = a.Pitch + (b.Pitch - a.Pitch) * k,
                Flags = k < 0.5 ? a.Flags : b.Flags,
                Health = b.Health,
                Armor = b.Armor,
            };
            return true;
        }
        // past the newest sample: extrapolate briefly (≤ 100 ms at 30 Hz) then hold
        var (tl, last) = t[^1];
        var ahead = System.Math.Min(renderTick - tl, 3.0) / 30.0;
        state = last with { Position = last.Position + last.Velocity * ahead };
        return true;
    }

    private static double LerpAngle(double a, double b, double k)
    {
        var d = (b - a) % (2 * System.Math.PI);
        if (d > System.Math.PI) d -= 2 * System.Math.PI;
        if (d < -System.Math.PI) d += 2 * System.Math.PI;
        return a + d * k;
    }
}
