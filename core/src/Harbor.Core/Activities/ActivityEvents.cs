using Harbor.Core.Events;
using Harbor.Core.Math;

namespace Harbor.Core.Activities;

public sealed record ContractStarted(int RunId, string ContractId, IReadOnlyList<int> Party) : ISimEvent;
public sealed record ContractEnded(int RunId, string ContractId, bool Success, string Reason, IReadOnlyList<int> Party, long TotalPaid) : ISimEvent;

public sealed record PrepCompleted(int CrewId, string HeistId, string PrepId) : ISimEvent;
public sealed record PrepLost(int CrewId, string HeistId, string PrepId) : ISimEvent;
/// <summary>Finale done; the crew leader has until the deadline to set shares (else equal split).</summary>
public sealed record HeistSplitPending(int RunId, string HeistId, IReadOnlyList<string> Members, IReadOnlyList<int> MemberActors, long Take, double SecondsLeft) : ISimEvent;
public sealed record HeistPaid(int RunId, string HeistId, IReadOnlyDictionary<string, long> Shares) : ISimEvent;

public sealed record TurfAttackScheduled(string ZoneId, int OwnerCrew, double StartsInSeconds) : ISimEvent;
public sealed record TurfAttackStarted(string ZoneId, int OwnerCrew, double WindowSeconds) : ISimEvent;
public sealed record TurfDefended(string ZoneId, int OwnerCrew) : ISimEvent;
public sealed record TurfCaptured(string ZoneId, int NewCrew, int OldCrew) : ISimEvent;
public sealed record TurfIncome(string ZoneId, int CrewId, long Amount) : ISimEvent;

public sealed record SupplyDropAnnounced(int DropId, string SiteId, Vec3d Position, double LandsInSeconds) : ISimEvent;
public sealed record SupplyDropLanded(int DropId, int CrateActorId, Vec3d Position) : ISimEvent;
public sealed record SupplyDropOpened(int DropId, int ActorId, long Cash) : ISimEvent;
public sealed record SupplyDropExpired(int DropId) : ISimEvent;

public sealed record BountyPlaced(int TargetActorId, string TargetProfile, long Amount, string Reason) : ISimEvent;
public sealed record BountyClaimed(int TargetActorId, int HunterActorId, long Amount) : ISimEvent;
public sealed record BountyCleared(int TargetActorId, string Reason) : ISimEvent;
