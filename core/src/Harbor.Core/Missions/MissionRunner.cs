using Harbor.Core.Events;
using Harbor.Core.Math;
using Harbor.Core.Sim;

namespace Harbor.Core.Missions;

public enum MissionStatus { Running, Succeeded, Failed }

public sealed record ObjectiveStarted(int RunId, int Index, ObjectiveType Type, string Label, Vec3d Position) : ISimEvent;
/// <summary>Published at 10 % steps so the HUD bar moves without flooding the wire.</summary>
public sealed record ObjectiveProgressed(int RunId, int Index, double Fraction) : ISimEvent;
public sealed record ItemDelivered(int RunId, int ItemId, int CarrierId, long Value) : ISimEvent;
public sealed record MissionEnded(int RunId, string Tag, bool Success, string Reason) : ISimEvent;

/// <summary>A running mission instance: a party working through an ordered list of objectives under a time limit.</summary>
public sealed class MissionRun
{
    public int Id { get; init; }
    public string Tag { get; init; } = "";
    public IReadOnlyList<ObjectiveDef> Objectives { get; init; } = Array.Empty<ObjectiveDef>();
    public double TimeLimit { get; init; }
    public List<int> Party { get; } = new();
    public double Elapsed { get; internal set; }
    public int Index { get; internal set; }
    public MissionStatus Status { get; internal set; }
    public string Reason { get; internal set; } = "";
    public long DeliveredValue { get; internal set; }
    public double TimeLeftFraction => TimeLimit <= 0 ? 1 : System.Math.Clamp(1 - Elapsed / TimeLimit, 0, 1);
    public ObjectiveDef? Current => Index < Objectives.Count ? Objectives[Index] : null;

    internal double Progress;
    internal int Delivered;
    internal int ProgressStep;
    internal bool CrimeReported;
    internal bool Entered;
}

/// <summary>Runs <see cref="MissionRun"/>s. Knows nothing about rewards: owners (Activities, single-player story)
/// subscribe to <see cref="MissionEnded"/>.</summary>
public sealed class MissionRunner(SimWorld world, CarrySystem carry, InteractInput input, Action<int, string>? reportCrime = null)
{
    /// <summary>Seconds since last damage before an interaction resumes.</summary>
    public double InterruptSeconds { get; init; } = 0.5;
    private readonly Dictionary<int, MissionRun> _runs = new();
    private int _next = 1;

    public IEnumerable<MissionRun> Active => _runs.Values;
    public MissionRun? Get(int runId) => _runs.GetValueOrDefault(runId);
    public MissionRun? RunOf(int actorId) => _runs.Values.FirstOrDefault(r => r.Party.Contains(actorId));

    public MissionRun Start(string tag, IReadOnlyList<ObjectiveDef> objectives, double timeLimit, IEnumerable<int> party)
    {
        var run = new MissionRun { Id = _next++, Tag = tag, Objectives = objectives, TimeLimit = timeLimit };
        run.Party.AddRange(party.Distinct());
        _runs[run.Id] = run;
        Enter(run);
        return run;
    }

    public void RemoveMember(int actorId)
    {
        var run = RunOf(actorId);
        if (run == null) return;
        run.Party.Remove(actorId);
        if (run.Party.Count == 0) End(run, false, "abandoned");
    }

    public void Abort(int runId, string reason)
    {
        if (_runs.TryGetValue(runId, out var run)) End(run, false, reason);
    }

    public void Step(double dt)
    {
        foreach (var run in _runs.Values.ToList())
        {
            run.Elapsed += dt;
            run.Party.RemoveAll(id => !world.Actors.TryGet(id, out _));
            if (run.Party.Count == 0) { End(run, false, "abandoned"); continue; }
            if (run.TimeLimit > 0 && run.Elapsed > run.TimeLimit) { End(run, false, "timeout"); continue; }
            if (run.Party.All(id => !world.Actors.Get(id).IsAlive)) { End(run, false, "wiped"); continue; }
            if (StepObjective(run, dt))
            {
                run.Index++;
                if (run.Index >= run.Objectives.Count) { End(run, true, "completed"); continue; }
                Enter(run);
            }
        }
    }

    private List<Actor> ActiveMembers(MissionRun run) =>
        run.Party.Select(id => world.Actors.Get(id)).Where(a => a.IsActive()).ToList();

    private void Enter(MissionRun run)
    {
        var o = run.Current!;
        run.Progress = 0; run.Delivered = 0; run.ProgressStep = 0; run.CrimeReported = false; run.Entered = true;
        if (o.Type == ObjectiveType.Deliver)
            for (var i = 0; i < o.Count; i++)
                carry.Spawn(o.Item, o.AtPos + new Vec3d((i % 4) * 0.7, 0, (i / 4) * 0.7), o.ValueEach, ItemTag(run));
        world.Events.Publish(new ObjectiveStarted(run.Id, run.Index, o.Type, o.Label,
            o.Type == ObjectiveType.Deliver ? o.ToPos : o.AtPos));
    }

    private static string ItemTag(MissionRun run) => $"mission:{run.Id}";

    private void Crime(MissionRun run, ObjectiveDef o, int actorId)
    {
        if (run.CrimeReported || o.Crime.Length == 0) return;
        run.CrimeReported = true;
        reportCrime?.Invoke(actorId, o.Crime);
    }

    /// <returns>true when the current objective is complete.</returns>
    private bool StepObjective(MissionRun run, double dt)
    {
        var o = run.Current!;
        var members = ActiveMembers(run);
        bool Inside(Actor a) => ActorQueries.FlatDistance(a.Position, o.AtPos) <= o.Radius;
        switch (o.Type)
        {
            case ObjectiveType.Reach:
                return o.All ? members.Count > 0 && members.All(Inside) : members.Any(Inside);

            case ObjectiveType.Interact:
            {
                var worker = members.FirstOrDefault(a => Inside(a) && input.IsHeld(a.Id) && a.Vitals.SecondsSinceDamage >= InterruptSeconds);
                if (worker == null)
                {
                    if (o.ResetOnInterrupt && run.Progress > 0) { run.Progress = 0; run.ProgressStep = 0; Publish(run, 0); }
                    return false;
                }
                Crime(run, o, worker.Id);
                return Advance(run, dt, o.Seconds);
            }

            case ObjectiveType.Hold:
            {
                var inside = members.FirstOrDefault(Inside);
                if (inside == null) return false;
                Crime(run, o, inside.Id);
                return Advance(run, dt, o.Seconds);
            }

            case ObjectiveType.Escape:
                return members.Count > 0 && members.All(a => ActorQueries.FlatDistance(a.Position, o.AtPos) > o.Distance);

            case ObjectiveType.Deliver:
            {
                foreach (var item in carry.WithTag(ItemTag(run)).ToList())
                {
                    if (item.CarrierId is not { } c || !run.Party.Contains(c)) continue;
                    Crime(run, o, c);
                    if (ActorQueries.FlatDistance(item.Position, o.ToPos) > o.Radius) continue;
                    carry.Remove(item.Id);
                    run.Delivered++;
                    run.DeliveredValue += item.Value;
                    world.Events.Publish(new ItemDelivered(run.Id, item.Id, c, item.Value));
                    Publish(run, (double)run.Delivered / o.Count);
                }
                return run.Delivered >= o.Count;
            }
        }
        return false;
    }

    private bool Advance(MissionRun run, double dt, double seconds)
    {
        run.Progress += dt;
        var f = System.Math.Min(1, run.Progress / seconds);
        var step = (int)(f * 10);
        if (step != run.ProgressStep) { run.ProgressStep = step; Publish(run, f); }
        return run.Progress >= seconds - 1e-9;
    }

    private void Publish(MissionRun run, double fraction) =>
        world.Events.Publish(new ObjectiveProgressed(run.Id, run.Index, fraction));

    private void End(MissionRun run, bool success, string reason)
    {
        run.Status = success ? MissionStatus.Succeeded : MissionStatus.Failed;
        run.Reason = reason;
        _runs.Remove(run.Id);
        foreach (var item in carry.WithTag(ItemTag(run)).ToList()) carry.Remove(item.Id);
        world.Events.Publish(new MissionEnded(run.Id, run.Tag, success, reason));
    }
}
