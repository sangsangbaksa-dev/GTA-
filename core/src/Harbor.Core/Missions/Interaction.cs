using Harbor.Core.Math;
using Harbor.Core.Sim;

namespace Harbor.Core.Missions;

/// <summary>Per-actor "interact" button state fed by the host every input tick. Hold = continuous action
/// (hack, open crate), press = one-shot (pick up a bag).</summary>
public sealed class InteractInput
{
    private readonly HashSet<int> _held = new();
    private readonly HashSet<int> _pressed = new();

    public void Set(int actorId, bool held)
    {
        if (held && !_held.Contains(actorId)) _pressed.Add(actorId);
        if (held) _held.Add(actorId); else _held.Remove(actorId);
    }

    public bool IsHeld(int actorId) => _held.Contains(actorId);
    public bool WasPressed(int actorId) => _pressed.Contains(actorId);
    public void Forget(int actorId) { _held.Remove(actorId); _pressed.Remove(actorId); }

    /// <summary>Called once per tick after every consumer ran.</summary>
    public void EndTick() => _pressed.Clear();
}

public static class ActorQueries
{
    /// <summary>Alive, not downed, not in passive mode — can take part in an activity this tick.</summary>
    public static bool IsActive(this Actor a) =>
        a.IsAlive && (a.Status & (ActorFlags.Downed | ActorFlags.Passive)) == 0;

    public static double FlatDistance(Vec3d a, Vec3d b) => (a - b).WithY(0).Length;

    public static Vec3d ToVec(this double[] p) => p.Length >= 3 ? new Vec3d(p[0], p[1], p[2]) : Vec3d.Zero;
}
