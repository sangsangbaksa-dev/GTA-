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
        var map = HarborLayout.Load(files, "data/world/harbor_front.json");
        var layout = map.CreateStaticWorld();
        var definition = StreetPopulationDef.Load(files, "data/world/harbor_population.json");

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
                        Assert.True(map.Roads.Any(road => road.Contains(p) && road.Kind != "path"),
                            $"{route.Id} at {p} is outside the drivable road");
                }
            }
        }

        var (_, population) = MakeWorld(definition);
        var state = population.GetState();
        Assert.Equal(definition.PedestrianRoutes.Sum(route => route.Count), state.Pedestrians.Length);
        Assert.Equal(definition.TrafficRoutes.Sum(route => route.Count), state.Traffic.Length);
        Assert.Equal(7, map.Districts.Count);
        foreach (var district in map.Districts)
        {
            bool Inside(Vec3d point) => System.Math.Abs(point.X - district.Center[0]) < district.Size[0] / 2
                && System.Math.Abs(point.Z - district.Center[1]) < district.Size[1] / 2;
            Assert.Contains(state.Pedestrians, ped => Inside(ped.Position));
            Assert.Contains(state.Traffic, car => Inside(car.Position));
        }
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
    public void TrafficWaitsAtRedAndAcceleratesAtGreen()
    {
        var route = Path("signal-loop", 8, true, [0, 0, 0], [20, 0, 0], [20, 0, 20], [0, 0, 20]) with
        {
            Signals = [new StreetSignalDef(0, CycleSeconds: 18, GreenSeconds: 5, OffsetSeconds: 6)]
        };
        var (world, population) = MakeWorld(new StreetPopulationDef { TrafficRoutes = [route] });

        Advance(world, 350);
        var stopped = Assert.Single(population.GetState().Traffic);
        Assert.InRange(stopped.Speed, 0, 0.4);
        Assert.InRange(stopped.Position.Z, 2.5, 4);

        Advance(world, 100);
        var released = Assert.Single(population.GetState().Traffic);
        Assert.InRange(released.Speed, 6, 8);
        Assert.True(released.Position.X > 2);
    }

    [Fact]
    public void AmbientActorsFollowTerrainElevation()
    {
        var level = new StaticWorld(terrainElevation: p => 8 + p.X * 0.02);
        var world = new SimWorld(new NullLog());
        var population = world.AddSystem(new StreetPopulation(new StreetPopulationDef
        {
            PedestrianRoutes = [Path("hill", 1.4, false, [0, 0, 0], [50, 0, 0])],
            TrafficRoutes = [Path("hill-road", 8, true, [0, 0, 0], [50, 0, 0], [50, 0, 30], [0, 0, 30])],
            Cargo = [new StreetCargoDef("hill-crate", [25, 0, 0], 10)]
        }, level));
        Advance(world, 20);
        var state = population.GetState();
        foreach (var p in state.Pedestrians.Select(p => p.Position).Concat(state.Traffic.Select(c => c.Position)).Concat(state.Cargo.Select(c => c.Position)))
            Assert.InRange(System.Math.Abs(p.Y - level.GroundElevation(p)), 0, 0.02);
    }

    [Fact]
    public void PedestrianReactsToApproachingCarButNotSlowOrDistantCar()
    {
        var (world, population) = MakeWorld(new StreetPopulationDef
        {
            FleeSeconds = 2,
            PedestrianRoutes = [Path("street", 1.4, false, [0, 0, 0], [150, 0, 0])]
        });
        var p = Assert.Single(population.GetState().Pedestrians).Position;
        population.VehicleThreats = () => [(p + new Vec3d(0, 0, 5), 0.0, 2.0), (p + new Vec3d(0, 6, 5), 0.0, 12.0)];
        Advance(world, 5);
        Assert.False(Assert.Single(population.GetState().Pedestrians).Fleeing);
        p = Assert.Single(population.GetState().Pedestrians).Position;
        population.VehicleThreats = () => [(p + new Vec3d(0, 0, 5), 0.0, 12.0)];
        Advance(world, 15);
        Assert.True(Assert.Single(population.GetState().Pedestrians).Fleeing);
        population.VehicleThreats = null;
        Advance(world, 80);
        Assert.False(Assert.Single(population.GetState().Pedestrians).Fleeing);
    }

    [Fact]
    public void PedestrianStepsOutOfVehicleLaneAndReturnsToFootpathAfterward()
    {
        var (world, population) = MakeWorld(new StreetPopulationDef
        {
            FleeSeconds = 2,
            PedestrianRoutes = [Path("footpath", 1.4, false, [-50, 0, 0], [50, 0, 0])],
        });
        var before = Assert.Single(population.GetState().Pedestrians);
        population.VehicleThreats = () => [(before.Position - new Vec3d(8, 0, 0), -System.Math.PI / 2, 12)];
        Advance(world, 23);
        var dodging = Assert.Single(population.GetState().Pedestrians);
        Assert.True(dodging.Fleeing);
        Assert.InRange(System.Math.Abs(dodging.Position.Z), 2, 2.6);

        population.VehicleThreats = null;
        Advance(world, 120);
        var recovered = Assert.Single(population.GetState().Pedestrians);
        Assert.Equal("walking", recovered.Reaction);
        Assert.Equal(0, recovered.Position.Z);
    }

    [Fact]
    public void OpposingPedestriansUseSeparateSidesOfTheFootpath()
    {
        var (world, population) = MakeWorld(new StreetPopulationDef
        {
            PedestrianRoutes = [Path("sidewalk", 1.4, false, [0, 0, 0], [8, 0, 0]) with { Count = 2 }],
        });
        var passed = false;
        for (var tick = 0; tick < 90; tick++)
        {
            Advance(world, 1);
            var walkers = population.GetState().Pedestrians;
            if (System.Math.Abs(walkers[0].Position.X - walkers[1].Position.X) > .35) continue;
            Assert.True(walkers[0].Position.Z * walkers[1].Position.Z < 0);
            passed = true;
            break;
        }
        Assert.True(passed);
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
