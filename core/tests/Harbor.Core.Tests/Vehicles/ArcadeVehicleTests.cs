using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Vehicles;
using Xunit;

namespace Harbor.Core.Tests.Vehicles;

public sealed class ArcadeVehicleTests
{
    [Fact]
    public void AcceleratesBrakesAndReversesFromRest()
    {
        var world = new StaticWorld();
        var car = new ArcadeVehicle(Vec3d.Zero);

        Step(car, world, new VehicleInput(1, 0, false), 60);
        var forwardZ = car.Position.Z;
        Assert.InRange(car.Speed, 6.7, 6.9);
        Assert.True(forwardZ < -3);

        Step(car, world, new VehicleInput(1, 0, true), 60);
        var stoppedZ = car.Position.Z;
        Assert.Equal(0, car.Speed);
        Assert.True(stoppedZ < forwardZ);

        Step(car, world, new VehicleInput(-1, 0, false), 60);
        Assert.InRange(car.Speed, -4.3, -4.1);
        Assert.True(car.Position.Z > stoppedZ);
    }

    [Fact]
    public void RightSteeringChangesHeadingAndWorldX()
    {
        var car = new ArcadeVehicle(Vec3d.Zero);
        Step(car, new StaticWorld(), new VehicleInput(1, 1, false), 60);

        Assert.True(car.Steering > 0);
        Assert.True(car.Yaw < 0);
        Assert.True(car.Position.X > 0);
        Assert.True(car.Position.Z < 0);
    }

    [Fact]
    public void ThinWallBlocksTranslationButCarCanReverseAway()
    {
        var world = new StaticWorld();
        world.Add(Aabb.FromCenter(new Vec3d(0, 1, -6), new Vec3d(12, 2, 0.05)), SurfaceMaterial.Concrete);
        var car = new ArcadeVehicle(Vec3d.Zero);

        Step(car, world, new VehicleInput(1, 0, false), 250);
        var contact = car.Position.Z;
        Assert.InRange(contact, -6 + 0.025 + ArcadeVehicle.HalfLength, -3.95);
        Assert.Equal(0, car.Speed);

        Step(car, world, new VehicleInput(-1, 0, false), 60);
        Assert.True(car.Position.Z > contact + 1);
    }

    [Fact]
    public void OrientedFootprintCanPassCornerWithinBroadPhase()
    {
        var world = new StaticWorld();
        world.Add(Aabb.FromCenter(new Vec3d(1.7, 1, -1.7), new Vec3d(0.1, 2, 0.1)), SurfaceMaterial.Concrete);
        var car = new ArcadeVehicle(Vec3d.Zero, System.Math.PI / 4);

        Step(car, world, new VehicleInput(1, 0, false), 60);

        Assert.InRange(car.Speed, 6.7, 6.9);
        Assert.True(car.Position.X < -2);
    }

    private static void Step(ArcadeVehicle car, StaticWorld world, VehicleInput input, int ticks)
    {
        for (int tick = 0; tick < ticks; tick++) car.Step(input, 1.0 / 60, world);
    }
}
