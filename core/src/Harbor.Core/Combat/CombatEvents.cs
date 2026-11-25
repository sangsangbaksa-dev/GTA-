using Harbor.Core.Events;
using Harbor.Core.Math;

namespace Harbor.Core.Combat;

/// <summary>A weapon was fired (feeds noise → AI awareness, Heat, client effects).</summary>
public sealed record ShotFired(int ShooterId, string WeaponId, Vec3d Origin, Vec3d End, double NoiseRadius) : ISimEvent;
public sealed record ActorDamaged(int VictimId, int AttackerId, int HealthLost, int ArmorLost, HitZone Zone, string Source) : ISimEvent;
public sealed record ActorDowned(int VictimId, int AttackerId) : ISimEvent;
public sealed record ActorKilled(int VictimId, int KillerId, string Source, bool Headshot) : ISimEvent;
public sealed record ActorRevived(int VictimId, int ReviverId) : ISimEvent;
public sealed record ActorRespawned(int ActorId, Vec3d Position) : ISimEvent;
public sealed record ExplosiveDetonated(int BoxId, Vec3d Position, int TriggeredBy) : ISimEvent;
/// <summary>Same attacker killed the same player too often in the window (griefing) — Heat/bounty react.</summary>
public sealed record RepeatKillDetected(int KillerId, int VictimId, int Count) : ISimEvent;

/// <summary>Combat-relevant slice of one player input (decoupled from Net, see ARCHITECTURE §2.1).</summary>
public readonly record struct CombatInput(bool Fire, bool Aim, bool Reload, bool Interact, byte Slot,
    double Yaw, double Pitch, long ViewTick);
