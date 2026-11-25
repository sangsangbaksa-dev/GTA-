using Harbor.Core.Data;
using Harbor.Core.Math;
using Harbor.Core.Ports;

namespace Harbor.Core.Server;

public enum HarborJobKind { Combat, Drive }
public enum HarborJobStatus { Active, Succeeded, Failed }
public enum HarborJobStep { Clear, Reach, Recover, Checkpoint, Park }

public sealed record HarborJobOffer(string Id, string Title, string Briefing, Vec3d StartPosition,
    double Radius, long Reward, HarborJobKind Kind, bool CanStart);

public sealed record HarborJobProgress(string Id, string Title, string Briefing, string Objective, Vec3d Waypoint,
    double Radius, int Stage, int StageCount, double RemainingSeconds, int RemainingEnemies, long Reward,
    HarborJobStatus Status, int RunId, HarborJobKind Kind, bool IsParticipant, HarborJobStep StepType, bool CanInteract);

public sealed record HarborJobResult(string Id, string Title, HarborJobStatus Status, long Reward,
    double Elapsed, int RunId, string Reason);

public sealed record HarborJobsState
{
    public HarborJobOffer[] Available { get; init; } = [];
    public HarborJobProgress? Active { get; init; }
    public HarborJobResult? LastResult { get; init; }
    public long Credits { get; init; }
}

public sealed record HarborJobStageDef
{
    public HarborJobStep Type { get; init; }
    public string Objective { get; init; } = "";
    public double[] At { get; init; } = [];
    public double Radius { get; init; }
    public double[][] Enemies { get; init; } = [];
    public Vec3d Position => HarborLockdownDef.Point(At);
}

public sealed record HarborJobDef
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Briefing { get; init; } = "";
    public HarborJobKind Kind { get; init; }
    public double[] Start { get; init; } = [];
    public double Radius { get; init; }
    public double TimeLimit { get; init; }
    public long Reward { get; init; }
    public HarborJobStageDef[] Stages { get; init; } = [];
    public Vec3d StartPosition => HarborLockdownDef.Point(Start);
}

public sealed record HarborJobCatalog
{
    public HarborJobDef[] Jobs { get; init; } = [];

    public static HarborJobCatalog Load(IFileSystem files, string path)
    {
        var catalog = Json.Read<HarborJobCatalog>(files, path);
        Json.Require(catalog.Jobs is { Length: > 0 and <= 12 }, $"{path}: expected 1–12 jobs");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in catalog.Jobs)
        {
            Json.Require(job != null, $"{path}: job must be an object");
            Json.Require(ValidText(job!.Id, 48) && ids.Add(job.Id) && ValidText(job.Title, 64) && ValidText(job.Briefing, 230),
                $"{path}: invalid or duplicate job metadata");
            Json.Require(Enum.IsDefined(job.Kind) && ValidPoint(job.Start) && ValidNumber(job.Radius, 35)
                && ValidNumber(job.TimeLimit, 900) && job.Reward is > 0 and <= 100_000,
                $"{path}: invalid layout or reward for {job.Id}");
            Json.Require(job.Stages is { Length: > 0 and <= 12 }, $"{path}: invalid stage count for {job.Id}");
            foreach (var stage in job.Stages)
            {
                Json.Require(stage != null && ValidText(stage.Objective, 120) && ValidPoint(stage.At)
                    && ValidNumber(stage.Radius, 35), $"{path}: invalid stage in {job.Id}");
                var compatible = job.Kind == HarborJobKind.Drive ? stage!.Type is HarborJobStep.Checkpoint or HarborJobStep.Park
                    : stage!.Type is HarborJobStep.Clear or HarborJobStep.Reach or HarborJobStep.Recover;
                Json.Require(compatible, $"{path}: {stage.Type} does not match {job.Kind}");
                Json.Require(stage.Type == HarborJobStep.Clear
                    ? stage.Enemies is { Length: > 0 and <= 6 } && stage.Enemies.All(ValidPoint)
                    : stage.Enemies is { Length: 0 }, $"{path}: invalid enemy spawns for {job.Id}");
            }
        }
        return catalog;
    }

    private static bool ValidText(string? value, int length) => !string.IsNullOrWhiteSpace(value) && value.Length <= length;
    private static bool ValidPoint(double[]? point) => point is { Length: 3 } && point.All(x => double.IsFinite(x) && System.Math.Abs(x) <= 10_000);
    private static bool ValidNumber(double value, double max) => value > 0 && value <= max;
}
