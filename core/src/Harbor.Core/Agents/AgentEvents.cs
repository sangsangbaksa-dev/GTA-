using Harbor.Core.Events;
using Harbor.Core.Math;

namespace Harbor.Core.Agents;

public sealed record NpcSpawned(int ActorId, string Archetype, string Faction, string Model) : ISimEvent;
public sealed record NpcKilled(int ActorId, string Archetype, string Faction, int KillerId, Vec3d Position) : ISimEvent;
/// <summary>The NPC actor was removed from the world (corpse cleanup, encounter deactivated, police stood down).</summary>
public sealed record NpcDespawned(int ActorId) : ISimEvent;
