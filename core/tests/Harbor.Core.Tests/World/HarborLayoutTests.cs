using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Ports;
using Harbor.Core.Server;
using Harbor.Core.Vehicles;
using Xunit;
using M = System.Math;

namespace Harbor.Core.Tests.World;

public sealed class HarborLayoutTests
{
    [Fact]
    public void SandboxRoadTripConnectsTheHarborAndAllSixDistricts()
    {
        var layout = Load();
        var level = layout.CreateStaticWorld();
        Assert.Equal(1290, layout.Bounds.Max[0] - layout.Bounds.Min[0]);
        Assert.Equal(1110, layout.Bounds.Max[1] - layout.Bounds.Min[1]);
        Assert.Equal(new[] { "city", "desert", "forest", "harbor", "low_income", "military", "wealthy" },
            layout.Districts.Select(d => d.Theme).Order(StringComparer.Ordinal));
        Assert.All(layout.Districts, district => Assert.Contains(layout.Landmarks, l => l.District == district.Id));

        Vec3d[] route =
        [
            layout.SpawnPosition, new(0, 0, 33), new(-580, 0, 33), new(-580, 0, 70),
            new(-590, 0, 128), new(-580, 0, 174), new(-562, 0, 224), new(-581, 0, 278),
            new(-581, 0, 361), new(-570, 0, 413), new(-541, 0, 438), new(-155, 0, 438),
            new(-155, 0, 224), new(-155, 0, 170), new(-155, 0, 95), new(-155, 0, 33),
            new(-155, 0, -195), new(-155, 0, -242), new(-155, 0, -440), new(-595, 0, -440),
            new(-155, 0, -440), new(-155, 0, -345), new(224, 0, -345), new(388, 0, -345),
            new(388, 0, -268), new(470, 0, -268),
        ];
        var vehicle = new VehicleShape { HalfWidth = 1.13, HalfLength = 2.41, RoofHeight = 1.8 };
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < route.Length - 1; i++)
        {
            var direction = route[i + 1] - route[i];
            var yaw = M.Atan2(-direction.X, -direction.Z);
            foreach (var point in Sample(route[i], route[i + 1]))
            {
                Assert.False(ArcadeVehicle.Blocked(level, point, yaw, vehicle), $"Vehicle hits a static obstacle at {point}");
                Assert.True(layout.Ground.Any(ground => Contains(ground, point))
                    || layout.Roads.Any(road => road.Kind == "bridge" && road.Contains(point)), $"No ground or bridge at {point}");
                if (i > 0)
                    Assert.Contains(layout.Roads, road => road.Contains(point));

                foreach (var district in layout.Districts)
                    if (Contains(district, point)) visited.Add(district.Id);
            }
        }

        Assert.True(visited.SetEquals(layout.Districts.Select(d => d.Id)));
    }

    [Fact]
    public void EveryAuthoredCenterlineHasClearanceAndMatchesItsFootprint()
    {
        var layout = Load();
        var level = layout.CreateStaticWorld();
        var roads = layout.Roads.Where(road => road.From != null && road.To != null).ToArray();
        Assert.NotEmpty(roads);
        foreach (var road in roads)
        {
            var from = Point(road.From!);
            var to = Point(road.To!);
            Assert.True(road.Contains(Vec3d.Lerp(from, to, .1)), $"{road.Name} start misses its footprint");
            Assert.True(road.Contains(Vec3d.Lerp(from, to, .9)), $"{road.Name} end misses its footprint");
            var clearance = road.Kind == "path" ? .36 : 2.45;
            foreach (var point in Sample(from, to))
            {
                var volume = new Aabb(point + new Vec3d(-clearance, 0.15, -clearance),
                    point + new Vec3d(clearance, 1.75, clearance));
                var obstruction = level.Overlapping(volume).FirstOrDefault();
                Assert.True(obstruction.Id == 0, $"{road.Name} at {point} overlaps {obstruction.Tag}");
            }
        }
    }

    private static HarborLayout Load() => HarborLayout.Load(new DiskFileSystem(Repo.Root), "data/world/harbor_front.json");

    private static Vec3d Point(double[] horizontal) => new(horizontal[0], 0, horizontal[1]);

    private static bool Contains(HarborArea area, Vec3d point) =>
        M.Abs(point.X - area.Center[0]) <= area.Size[0] / 2 && M.Abs(point.Z - area.Center[1]) <= area.Size[1] / 2;

    private static IEnumerable<Vec3d> Sample(Vec3d from, Vec3d to)
    {
        var steps = (int)M.Ceiling(Vec3d.Distance(from, to) * 2);
        for (var i = 0; i <= steps; i++)
            yield return Vec3d.Lerp(from, to, (double)i / steps);
    }
}
