using Harbor.Core.Math;

namespace Harbor.Core.Sim;

public enum ActorKind : byte { Player = 1, Npc = 2, Pickup = 3, Loot = 4, Vehicle = 5 }

/// <summary>Replicated status bits. Movement-derived bits (Crouched/Aiming/Sprinting) come from
/// <see cref="MoveState"/>; the rest are set by gameplay modules.</summary>
[Flags]
public enum ActorFlags : byte
{
    None = 0,
    Crouched = 1 << 0,
    Aiming = 1 << 1,
    Sprinting = 1 << 2,
    Downed = 1 << 3,
    Dead = 1 << 4,
    Carrying = 1 << 5,
    Passive = 1 << 6,
}

/// <summary>Kinematic state advanced by <see cref="Movement.Step"/> on server and (predicted) client.</summary>
public struct MoveState
{
    public Vec3d Position;
    public Vec3d Velocity;
    public double Yaw;
    public double Pitch;
    public bool Crouched;
    public bool Aiming;
    public bool Sprinting;

    public readonly ActorFlags Flags =>
        (Crouched ? ActorFlags.Crouched : 0) | (Aiming ? ActorFlags.Aiming : 0) | (Sprinting ? ActorFlags.Sprinting : 0);
}

/// <summary>Health/armor block. Rules that change it live in Combat.</summary>
public struct Vitals
{
    public int Health;
    public int MaxHealth;
    public int Armor;
    public double SecondsSinceDamage;
    public double DownedSecondsLeft;

    public static Vitals Full(int maxHealth = 100) => new() { Health = maxHealth, MaxHealth = maxHealth };
}

/// <summary>A simulated entity (player avatar, NPC, pickup, loot bag...).</summary>
public sealed class Actor(int id, ActorKind kind)
{
    public int Id { get; } = id;
    public ActorKind Kind { get; } = kind;
    /// <summary>Network peer that controls this actor, or -1 for server-owned.</summary>
    public int OwnerPeer { get; set; } = -1;
    public int Team { get; set; }
    public string Name { get; set; } = "";
    public MoveState Move;
    public Vitals Vitals = Vitals.Full();
    public ActorFlags Status { get; set; }

    public Vec3d Position { get => Move.Position; set => Move.Position = value; }
    public ActorFlags Flags => Move.Flags | Status;
    public bool IsAlive => (Status & ActorFlags.Dead) == 0;
}

/// <summary>Owns all actors of a <see cref="SimWorld"/>. IDs are never reused within a session.</summary>
public sealed class ActorRegistry
{
    private readonly Dictionary<int, Actor> _actors = new();
    private int _nextId = 1;

    public Actor Spawn(ActorKind kind, Vec3d position)
    {
        var a = new Actor(_nextId++, kind);
        a.Move.Position = position;
        _actors[a.Id] = a;
        return a;
    }

    public bool Remove(int id) => _actors.Remove(id);
    public bool TryGet(int id, out Actor actor) => _actors.TryGetValue(id, out actor!);
    public Actor Get(int id) => _actors[id];
    public IReadOnlyCollection<Actor> All => _actors.Values;
    public int Count => _actors.Count;
}
