using Harbor.Core.Combat;
using Harbor.Core.Data;
using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Ports;

namespace Harbor.Core.Server;

public sealed record HarborObstacle
{
    public double[] Center { get; init; } = [];
    public double[] Size { get; init; } = [];
    public string Material { get; init; } = "concrete";
    public string Tag { get; init; } = "";
}

public sealed record HarborLayout
{
    public double[] Spawn { get; init; } = [];
    public IReadOnlyList<HarborObstacle> Obstacles { get; init; } = [];
    public Vec3d SpawnPosition => HarborLockdownDef.Point(Spawn);

    public static HarborLayout Load(IFileSystem files, string path)
    {
        var layout = Json.Read<HarborLayout>(files, path);
        Json.Require(ValidVector(layout.Spawn), $"{path}: spawn needs three finite coordinates");
        Json.Require(layout.Obstacles != null, $"{path}: obstacles are required");
        foreach (var obstacle in layout.Obstacles!)
        {
            Json.Require(obstacle != null && ValidVector(obstacle.Center) && ValidVector(obstacle.Size)
                && obstacle.Size.All(x => x > 0), $"{path}: obstacle has invalid center or size");
            MaterialTable.ParseId(obstacle!.Material);
        }
        return layout;
    }

    public StaticWorld CreateStaticWorld()
    {
        var world = new StaticWorld();
        foreach (var obstacle in Obstacles)
            world.Add(Aabb.FromCenter(HarborLockdownDef.Point(obstacle.Center), HarborLockdownDef.Point(obstacle.Size)),
                MaterialTable.ParseId(obstacle.Material), obstacle.Tag);
        return world;
    }

    private static bool ValidVector(double[]? point) => point is { Length: 3 } && point.All(double.IsFinite);
}
