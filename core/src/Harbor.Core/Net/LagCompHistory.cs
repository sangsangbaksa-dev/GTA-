using Harbor.Core.Math;
using Harbor.Core.Sim;

namespace Harbor.Core.Net;

public readonly record struct PoseSample(Vec3d Position, bool Crouched, bool Alive);

/// <summary>Ring buffer of past actor poses so the server can judge a shot against what the
/// shooter actually saw (lag compensation, ADR 0004). Rewind is clamped to a maximum age.</summary>
public sealed class LagCompHistory
{
    private readonly (long Tick, Dictionary<int, PoseSample> Poses)[] _frames;
    private long _latest = -1;

    public LagCompHistory(int capacityTicks)
    {
        _frames = new (long, Dictionary<int, PoseSample>)[capacityTicks];
        for (var i = 0; i < capacityTicks; i++) _frames[i] = (-1, new Dictionary<int, PoseSample>());
    }

    public long LatestTick => _latest;
    public long OldestTick => System.Math.Max(0, _latest - _frames.Length + 1);

    public void Record(long tick, IEnumerable<Actor> actors)
    {
        var slot = (int)(tick % _frames.Length);
        var poses = _frames[slot].Poses;
        poses.Clear();
        foreach (var a in actors)
            if (a.Kind is ActorKind.Player or ActorKind.Npc)
                poses[a.Id] = new PoseSample(a.Position, a.Move.Crouched, a.IsAlive);
        _frames[slot] = (tick, poses);
        _latest = tick;
    }

    /// <summary>Clamp a client's claimed view tick into [now - maxRewind, now].</summary>
    public long ClampTick(long requested, long now, int maxRewindTicks)
        => System.Math.Clamp(requested, System.Math.Max(OldestTick, now - maxRewindTicks), now);

    public bool TryGet(long tick, int actorId, out PoseSample pose)
    {
        var f = _frames[(int)(((tick % _frames.Length) + _frames.Length) % _frames.Length)];
        if (f.Tick == tick && f.Poses.TryGetValue(actorId, out pose)) return true;
        pose = default;
        return false;
    }

    /// <summary>All poses at a tick (actors that existed then).</summary>
    public IReadOnlyDictionary<int, PoseSample>? Frame(long tick)
    {
        var f = _frames[(int)(((tick % _frames.Length) + _frames.Length) % _frames.Length)];
        return f.Tick == tick ? f.Poses : null;
    }
}
