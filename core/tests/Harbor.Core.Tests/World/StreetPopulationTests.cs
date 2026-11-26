using Harbor.Core.Combat;
using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Ports;
using Harbor.Core.Server;
using Harbor.Core.Sim;
using Harbor.Core.World;
using Xunit;

namespace Harbor.Core.Tests.World;

public sealed class StreetPopulationTests
{
    [Fact]
    public void PublishedRoutesAreClearAndTrafficStaysOnRoads()
    {
        var files = new DiskFileSystem(Repo.Root);
        var layout = HarborLayout.Load(files, "data/world/harbor_front.json").CreateStaticWorld();
        var definition = StreetPopulationDef.Load(files, "data/world/harbor_population.json");
        using var map = System.Text.Json.JsonDocument.Parse(files.ReadAllText("data/world/harbor_front.json"));
        var roads = map.RootElement.GetProperty("roads").EnumerateArray().Select(road =>
        {
            var p = road.GetProperty("center");
            var s = road.GetProperty("size");
            return Aabb.FromCenter(new Vec3d(p[0].GetDouble(), 0, p[1].GetDouble()),
                new Vec3d(s[0].GetDouble(), 2, s[1].GetDouble()));
        }).ToArray();

        var paths = definition.PedestrianRoutes.Select(route => (Route: route, Traffic: false))
            .Concat(definition.TrafficRoutes.Select(route => (Route: route, Traffic: true)));
        foreach (var (route, traffic) in paths)
        {
            var points = route.Points.Select(point => new Vec3d(point[0], point[1], point[2])).ToArray();
            var segmentCount = route.Loop ? points.Length : points.Length - 1;
            var clearance = traffic ? 1.1 : 0.36;
            for (var i = 0; i < segmentCount; i++)
            {
                var from = points[i];
                var to = points[(i + 1) % points.Length];
                var slices = (int)System.Math.Ceiling(Vec3d.Distance(from, to) * 4);
                for (var step = 0; step <= slices; step++)
                {
                    var p = Vec3d.Lerp(from, to, (double)step / slices);
                    var volume = Aabb.FromCenter(p + new Vec3d(0, 0.9, 0), new Vec3d(clearance * 2, 1.5, clearance * 2));
                    var collision = layout.Overlapping(volume).FirstOrDefault();
                    Assert.True(collision.Id == 0, $"{route.Id} at {p} overlaps {collision.Tag}");
                    if (traffic)
                        Assert.Contains(roads, road => p.X >= road.Min.X && p.X <= road.Max.X
                            && p.Z >= road.Min.Z && p.Z <= road.Max.Z);
                }
            }
        }

        var (_, population) = MakeWorld(definition);
        var state = population.GetState();
        Assert.Equal(20, state.Pedestrians.Length);
        Assert.Equal(7, state.Traffic.Length);
        Assert.InRange(HarborProtocol.Encode(new HarborState { Ambient = state }).Length, 1, TcpTransport.MaxPacketBytes / 2);
    }

    [Fact]
    public void SameSeedAndTicksYieldSameActorsAndObserverCullKeepsStableIds()
    {
        var definition = StreetPopulationDef.Load(new DiskFileSystem(Repo.Root), "data/world/harbor_population.json");
        var (a, first) = MakeWorld(definition);
        var (b, second) = MakeWorld(definition);
        var start = first.GetState();
        Advance(a, 190);
        Advance(b, 190);
        var result = first.GetState();
        Assert.Equal(result.Pedestrians, second.GetState().Pedestrians);
        Assert.Equal(result.Traffic, second.GetState().Traffic);
        Assert.NotEqual(start.Pedestrians[0].Position, result.Pedestrians[0].Position);
        var nearby = first.GetState(new Vec3d(0, 0, 5));
        Assert.InRange(nearby.Pedestrians.Length, 1, result.Pedestrians.Length - 1);
        Assert.All(nearby.Pedestrians, p => Assert.Contains(p, result.Pedestrians));
    }

    [Fact]
    public void CiviliansReverseAndFleeFromAudibleShotThenRecover()
    {
        var definition = new StreetPopulationDef
        {
            HearingRadius = 15,
            FleeSeconds = 2,
            PedestrianRoutes =
            [
                Path("near", 1.4, false, [0, 0, 0], [150, 0, 0]),
                Path("far", 1.4, false, [0, 0, -100], [150, 0, -100])
            ]
        };
        var (world, population) = MakeWorld(definition);
        var before = population.GetState().Pedestrians[0];
        var direction = Vec3d.FromYawPitch(before.Yaw, 0);
        var gun = before.Position + direction * 3;

        world.Events.Publish(new ShotFired(100, "test", gun, gun, 1));
        Assert.All(population.GetState().Pedestrians, p => Assert.False(p.Fleeing));
        world.Events.Publish(new ShotFired(100, "test", gun, gun, 20));
        Advance(world, 15);
        var during = population.GetState().Pedestrians;
        Assert.True(during[0].Fleeing);
        Assert.False(during[1].Fleeing);
        Assert.Equal(definition.FleeSpeed, during[0].Speed);
        Assert.True((during[0].Position - before.Position).Dot(direction) < -0.5);

        Advance(world, 65);
        Assert.All(population.GetState().Pedestrians, p => Assert.False(p.Fleeing));
        Assert.InRange(population.GetState().Pedestrians[0].Speed, 1.2, 1.6);
    }

    [Fact]
    public void AmbientTrafficBrakesForAPlayerAndResumesWhenClear()
    {
        var definition = new StreetPopulationDef
        {
            TrafficRoutes =
            [
                Path("ring", 8, true, [-60, 0, -60], [60, 0, -60], [60, 0, 60], [-60, 0, 60])
            ]
        };
        var (world, population) = MakeWorld(definition);
        var starter = Assert.Single(population.GetState().Traffic);
        var blocker = world.Actors.Spawn(ActorKind.Player, starter.Position + Vec3d.FromYawPitch(starter.Yaw, 0) * 9);
        Advance(world, 170);
        var yielded = Assert.Single(population.GetState().Traffic);
        Assert.InRange(yielded.Speed, 0, 0.3);
        Assert.InRange(Vec3d.Distance(yielded.Position, blocker.Position), 2.5, 4);
        world.Actors.Remove(blocker.Id);
        Advance(world, 100);
        Assert.InRange(Assert.Single(population.GetState().Traffic).Speed, 7, starter.Speed);
    }

    [Fact]
    public void WalletIsTakenOnlyOnceAndAssaultMakesCivilianFlee()
    {
        var definition = new StreetPopulationDef
        {
            PedestrianRoutes = [Path("street", 1.4, false, [0, 0, 0], [50, 0, 0])]
        };
        var (world, population) = MakeWorld(definition);
        var victim = Assert.Single(population.GetState().Pedestrians);
        var player = world.Actors.Spawn(ActorKind.Player, victim.Position);
        Assert.True(population.TryStreetCrime(player, false, _ => true, out var cash, out var reported, out _));
        Assert.True(cash > 0);
        Assert.False(reported);
        Assert.False(population.TryStreetCrime(player, false, _ => true, out _, out _, out _));
        Assert.True(population.TryStreetCrime(player, true, _ => true, out _, out reported, out _));
        Assert.True(reported);
        Assert.True(Assert.Single(population.GetState().Pedestrians).Fleeing);
        population.ResetCrimes();
        Assert.True(population.TryStreetCrime(player, false, _ => true, out _, out _, out _));
    }

    private static StreetRouteDef Path(string id, double speed, bool loop, params double[][] points)
        => new() { Id = id, Count = 1, Speed = speed, Loop = loop, Points = points };

    private static (SimWorld, StreetPopulation) MakeWorld(StreetPopulationDef definition)
    {
        var world = new SimWorld(new NullLog());
        return (world, world.AddSystem(new StreetPopulation(definition)));
    }

    private static void Advance(SimWorld world, int ticks)
    {
        for (var i = 0; i < ticks; i++) world.Update(world.Loop.StepSeconds);
    }
}
