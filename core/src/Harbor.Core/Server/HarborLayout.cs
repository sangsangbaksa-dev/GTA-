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

public sealed record HarborBounds
{
    public double[] Min { get; init; } = [-215, -195];
    public double[] Max { get; init; } = [145, 95];
}

public record HarborArea
{
    public double[] Center { get; init; } = [];
    public double[] Size { get; init; } = [];
    public string Theme { get; init; } = "";
}

public sealed record HarborRoad : HarborArea
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "street";
    public double Yaw { get; init; }
    public double[]? From { get; init; }
    public double[]? To { get; init; }

    /// <summary>Checks the horizontal footprint, with positive Y-axis rotation in degrees.</summary>
    public bool Contains(Vec3d point)
    {
        var x = point.X - Center[0];
        var z = point.Z - Center[1];
        var (sin, cos) = System.Math.SinCos(Yaw * System.Math.PI / 180);
        return System.Math.Abs(x * cos - z * sin) <= Size[0] / 2
            && System.Math.Abs(x * sin + z * cos) <= Size[1] / 2;
    }
}

public sealed record HarborDistrict : HarborArea
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed record HarborLandmark
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string District { get; init; } = "";
    public string Kind { get; init; } = "";
    public double[] Position { get; init; } = [];
}

public sealed record HarborTerrain : HarborArea
{
    public double Height { get; init; }

    public double ElevationAt(Vec3d point)
    {
        var x = 2 * (point.X - Center[0]) / Size[0];
        var z = 2 * (point.Z - Center[1]) / Size[1];
        var falloff = System.Math.Max(0, 1 - x * x - z * z);
        return Height * falloff * falloff;
    }
}

public sealed record HarborLayout
{
    public double[] Spawn { get; init; } = [];
    public HarborBounds Bounds { get; init; } = new();
    public double WaterLevel { get; init; } = -1.12;
    public IReadOnlyList<HarborRoad> Roads { get; init; } = [];
    public IReadOnlyList<HarborArea> Ground { get; init; } = [];
    public IReadOnlyList<HarborArea> Water { get; init; } = [];
    public IReadOnlyList<HarborDistrict> Districts { get; init; } = [];
    public IReadOnlyList<HarborLandmark> Landmarks { get; init; } = [];
    public IReadOnlyList<HarborTerrain> Terrain { get; init; } = [];
    public IReadOnlyList<HarborObstacle> Obstacles { get; init; } = [];
    public Vec3d SpawnPosition => HarborLockdownDef.Point(Spawn);

    public static HarborLayout Load(IFileSystem files, string path)
    {
        var layout = Json.Read<HarborLayout>(files, path);
        Json.Require(ValidVector(layout.Spawn), $"{path}: spawn needs three finite coordinates");
        Json.Require(layout.Bounds != null && ValidVector(layout.Bounds.Min, 2) && ValidVector(layout.Bounds.Max, 2)
            && layout.Bounds.Max[0] > layout.Bounds.Min[0] && layout.Bounds.Max[1] > layout.Bounds.Min[1],
            $"{path}: map bounds need an ordered XZ extent");
        Json.Require(double.IsFinite(layout.WaterLevel), $"{path}: water level must be finite");
        Json.Require(layout.Ground != null && layout.Ground.All(ValidArea) && layout.Water != null && layout.Water.All(ValidArea),
            $"{path}: ground or water has an invalid footprint");
        Json.Require(layout.Terrain != null && layout.Terrain.All(patch => ValidArea(patch)
            && double.IsFinite(patch.Height) && patch.Height > 0 && patch.Height <= 60), $"{path}: invalid terrain elevations");
        Json.Require(layout.Roads != null && layout.Roads.All(r => ValidArea(r) && double.IsFinite(r.Yaw)
            && ValidLine(r)), $"{path}: road has an invalid footprint or centerline");
        Json.Require(layout.Districts != null && layout.Districts.All(d => ValidArea(d) && !string.IsNullOrWhiteSpace(d.Id)
            && !string.IsNullOrWhiteSpace(d.Name) && !string.IsNullOrWhiteSpace(d.Theme)), $"{path}: invalid district");
        var ids = layout.Districts!.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        Json.Require(ids.Count == layout.Districts!.Count, $"{path}: duplicate district ID");
        Json.Require(layout.Landmarks != null && layout.Landmarks.All(l => l != null && ValidVector(l.Position)
            && !string.IsNullOrWhiteSpace(l.Id) && !string.IsNullOrWhiteSpace(l.Name) && ids.Contains(l.District)),
            $"{path}: landmark needs a position, name and district");
        Json.Require(layout.Obstacles != null, $"{path}: obstacles are required");
        foreach (var obstacle in layout.Obstacles!)
        {
            Json.Require(obstacle != null && ValidVector(obstacle.Center) && ValidSize(obstacle.Size, 3),
                $"{path}: obstacle has invalid center or size");
            MaterialTable.ParseId(obstacle!.Material);
        }
        return layout;
    }

    public StaticWorld CreateStaticWorld()
    {
        var world = new StaticWorld(terrainElevation: Terrain.Count > 0 ? ElevationAt : null);
        foreach (var obstacle in Obstacles)
            world.Add(Aabb.FromCenter(HarborLockdownDef.Point(obstacle.Center), HarborLockdownDef.Point(obstacle.Size)),
                MaterialTable.ParseId(obstacle.Material), obstacle.Tag);
        return world;
    }

    public double ElevationAt(Vec3d point)
    {
        var height = 0d;
        foreach (var patch in Terrain)
            height = System.Math.Max(height, patch.ElevationAt(point));
        return height;
    }

    private static bool ValidArea(HarborArea? area) => area != null && ValidVector(area.Center, 2) && ValidSize(area.Size, 2);

    private static bool ValidLine(HarborRoad road) => road.From == null && road.To == null
        || ValidVector(road.From, 2) && ValidVector(road.To, 2) && !road.From!.SequenceEqual(road.To!);

    private static bool ValidSize(double[]? size, int dimensions) => ValidVector(size, dimensions) && size!.All(x => x > 0);

    private static bool ValidVector(double[]? point, int dimensions = 3) => point?.Length == dimensions && point.All(double.IsFinite);
}
