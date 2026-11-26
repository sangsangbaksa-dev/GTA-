using System.Globalization;
using Harbor.Core.Economy;
using Harbor.Core.Math;
using Harbor.Core.Missions;
using Harbor.Core.Net;
using Harbor.Core.Sim;

namespace Harbor.Core.Server;

/// <summary>Runs one shared district job at a time. Progress and payouts use server-owned actors only.</summary>
public sealed class HarborJobs(HarborJobCatalog catalog, HarborLockdown lockdown) : ISimSystem
{
    private const double PartnerRadius = 30;
    private const double ParkingSpeed = 1.25;
    private readonly Ledger _ledger = new();
    private readonly Dictionary<int, HarborJobResult> _results = new();
    private SimWorld _world = null!;
    private Run? _run;
    private int _nextRunId;

    public bool IsActive => _run != null;
    public bool IsCombat => _run?.Job.Kind == HarborJobKind.Combat;
    public bool IsParticipant(int actorId) => _run?.Participants.Contains(actorId) == true;
    public bool CanDrive(int actorId) => _run is not { Job.Kind: HarborJobKind.Drive } active || actorId == active.OwnerId;

    private sealed class Run(int id, HarborJobDef job, int ownerId, HashSet<int> participants)
    {
        public int Id { get; } = id;
        public HarborJobDef Job { get; } = job;
        public int OwnerId { get; } = ownerId;
        public HashSet<int> Participants { get; } = participants;
        public int Stage { get; set; }
        public double Elapsed { get; set; }
        public int[] Enemies { get; set; } = [];
        public HarborJobStageDef Current => Job.Stages[Stage];
    }

    public void Initialize(SimWorld world)
    {
        _world = world;
        world.Events.Subscribe<PlayerLeft>(left => _results.Remove(left.ActorId));
    }

    public HarborJobsState GetState(int actorId)
    {
        _world.Actors.TryGet(actorId, out var actor);
        var available = catalog.Jobs.Select(job => new HarborJobOffer(job.Id, job.Title, job.Briefing, job.StartPosition,
            job.Radius, job.Reward, job.Kind, actor != null && CanStart(actor, job))).ToArray();
        HarborJobProgress? progress = null;
        if (_run is { } active)
        {
            var stage = active.Current;
            progress = new HarborJobProgress(active.Job.Id, active.Job.Title, active.Job.Briefing, stage.Objective,
                stage.Position, stage.Radius, active.Stage + 1, active.Job.Stages.Length,
                System.Math.Max(0, active.Job.TimeLimit - active.Elapsed), CountEnemies(active), active.Job.Reward,
                HarborJobStatus.Active, active.Id, active.Job.Kind, active.Participants.Contains(actorId), stage.Type,
                actor != null && CanRecover(active, actor));
        }
        return new HarborJobsState
        {
            Available = available, Active = progress, Credits = _ledger.Balance(CreditAccount(actorId), Currency.Clean),
            LastResult = _results.GetValueOrDefault(actorId),
        };
    }

    public bool TryStart(Actor actor)
    {
        var job = catalog.Jobs.Where(candidate => CanStart(actor, candidate))
            .MinBy(candidate => ActorQueries.FlatDistance(actor.Position, candidate.StartPosition));
        if (job == null) return false;

        var participants = job.Kind == HarborJobKind.Drive ? new HashSet<int> { actor.Id } :
            _world.Actors.All.Where(other => other.Kind == ActorKind.Player && other.IsAlive
                && ActorQueries.FlatDistance(other.Position, job.StartPosition) <= PartnerRadius).Select(other => other.Id).ToHashSet();
        _run = new Run(++_nextRunId, job, actor.Id, participants);
        foreach (var id in participants) _results.Remove(id);
        if (job.Kind == HarborJobKind.Combat) lockdown.PrepareJobCombat();
        StartStage(_run);
        return true;
    }

    public bool TryRecover(Actor actor)
    {
        if (_run is not { } active || !CanRecover(active, actor)) return false;
        Advance(active);
        return true;
    }

    public void Step(double dt)
    {
        if (_run is not { } active) return;
        active.Elapsed += dt;
        if (active.Elapsed >= active.Job.TimeLimit)
        {
            End(active, HarborJobStatus.Failed, "Time expired.");
            return;
        }
        if (!active.Participants.Any(id => _world.Actors.TryGet(id, out var actor) && actor.IsAlive))
        {
            End(active, HarborJobStatus.Failed, "No crew members are still active.");
            return;
        }

        var stage = active.Current;
        switch (stage.Type)
        {
            case HarborJobStep.Clear when CountEnemies(active) == 0:
                Advance(active);
                break;
            case HarborJobStep.Reach when ParticipantsAt(active, stage):
                Advance(active);
                break;
            case HarborJobStep.Checkpoint when DriverAt(active, stage):
                Advance(active);
                break;
            case HarborJobStep.Park when DriverAt(active, stage) && System.Math.Abs(lockdown.GetVehicleSpeed(active.OwnerId)) <= ParkingSpeed:
                Advance(active);
                break;
        }
    }

    public void Cancel()
    {
        if (_run is { } active) End(active, HarborJobStatus.Failed, "Job was called off.");
    }

    private bool CanStart(Actor actor, HarborJobDef job) =>
        _run == null && lockdown.Phase == LockdownPhase.FreeRoam && actor.Kind == ActorKind.Player && actor.IsAlive
        && lockdown.IsDriving(actor.Id) == (job.Kind == HarborJobKind.Drive)
        && At(actor, job.StartPosition, job.Radius);

    private bool CanRecover(Run active, Actor actor) => active.Current.Type == HarborJobStep.Recover
        && active.Participants.Contains(actor.Id) && !lockdown.IsDriving(actor.Id)
        && At(actor, active.Current.Position, active.Current.Radius);

    private bool ParticipantsAt(Run active, HarborJobStageDef stage) =>
        active.Participants.Any(id => _world.Actors.TryGet(id, out var actor) && At(actor, stage.Position, stage.Radius));

    private bool DriverAt(Run active, HarborJobStageDef stage) =>
        lockdown.IsDriving(active.OwnerId) && _world.Actors.TryGet(active.OwnerId, out var driver) && driver.IsAlive
        && ActorQueries.FlatDistance(driver.Position, stage.Position) <= stage.Radius;

    private int CountEnemies(Run active) =>
        active.Enemies.Count(id => _world.Actors.TryGet(id, out var actor) && actor.IsAlive);

    private static bool At(Actor actor, Vec3d point, double radius) =>
        actor.IsAlive && ActorQueries.FlatDistance(actor.Position, point) <= radius;

    private void StartStage(Run active)
    {
        active.Enemies = active.Current.Type == HarborJobStep.Clear ? lockdown.SpawnJobEnemies(active.Current.Enemies) : [];
    }

    private void Advance(Run active)
    {
        if (active.Stage + 1 >= active.Job.Stages.Length)
        {
            End(active, HarborJobStatus.Succeeded, "");
            return;
        }
        lockdown.RemoveJobEnemies(active.Enemies);
        active.Stage++;
        StartStage(active);
    }

    private void End(Run active, HarborJobStatus status, string reason)
    {
        lockdown.RemoveJobEnemies(active.Enemies);
        foreach (var id in active.Participants)
        {
            if (!_world.Actors.TryGet(id, out var actor)) continue;
            var reward = status == HarborJobStatus.Succeeded && actor.IsAlive ? active.Job.Reward : 0;
            if (reward > 0)
            {
                var tx = new Transfer($"district-job:{active.Id}:{id}", AccountId.Faucet("district-jobs"), CreditAccount(id),
                    reward, Currency.Clean, active.Job.Id);
                if (!_ledger.Post(tx).Ok) reward = 0;
            }
            _results[id] = new HarborJobResult(active.Job.Id, active.Job.Title, status, reward, active.Elapsed, active.Id, reason);
        }
        _run = null;
    }

    private static AccountId CreditAccount(int actorId) => AccountId.Bank(actorId.ToString(CultureInfo.InvariantCulture));
}
