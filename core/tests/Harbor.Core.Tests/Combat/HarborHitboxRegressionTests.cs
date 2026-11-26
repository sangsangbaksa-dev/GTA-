using Harbor.Core.Combat;
using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Sim;
using Xunit;

namespace Harbor.Core.Tests.Combat;

public sealed class HarborHitboxRegressionTests
{
    [Fact]
    public void SteepRayHitsHeadEvenWhenFeetAreBeyondRange()
    {
        var pose = new PoseSample(Vec3d.Zero, false, true);
        Assert.True(Hitboxes.Raycast(new Vec3d(0, 8, 0), new Vec3d(0, -1, 0), 6.5, pose,
            out var distance, out var zone));
        Assert.Equal(HitZone.Head, zone);
        Assert.InRange(distance, 6.22, 6.24);
    }

    [Fact]
    public void InsideSphereAndCapsuleCountAsImmediateContact()
    {
        Assert.Equal(0d, Hitboxes.Sphere(Vec3d.Zero, new Vec3d(1, 0, 0), Vec3d.Zero, 0.2));
        Assert.Equal(0d, Hitboxes.Capsule(new Vec3d(0, 1.1, 0), new Vec3d(1, 0, 0),
            new Vec3d(0, 1, 0), new Vec3d(0, 1.3, 0), 0.24));
        Assert.True(Hitboxes.Raycast(new Vec3d(0, 1.1, 0), new Vec3d(1, 0, 0), 2,
            new PoseSample(Vec3d.Zero, false, true), out var distance, out var zone));
        Assert.Equal(0d, distance);
        Assert.Equal(HitZone.Torso, zone);
    }

    [Fact]
    public void CrouchingDoesNotLeaveAStandingHeadHitbox()
    {
        var origin = new Vec3d(0, 1.64, 3);
        var direction = new Vec3d(0, 0, -1);
        Assert.True(Hitboxes.Raycast(origin, direction, 5, new PoseSample(Vec3d.Zero, false, true),
            out _, out var zone));
        Assert.Equal(HitZone.Head, zone);
        Assert.False(Hitboxes.Raycast(origin, direction, 5, new PoseSample(Vec3d.Zero, true, true),
            out _, out _));
        Assert.False(Hitboxes.Raycast(new Vec3d(0.6, 1.2, 3), direction, 5,
            new PoseSample(Vec3d.Zero, false, true), out _, out _));
    }

    [Fact]
    public void LongSprintStepCannotPassThroughThinWall()
    {
        var world = new StaticWorld();
        world.Add(Aabb.FromCenter(new Vec3d(0, 1, -1.5), new Vec3d(8, 2, 0.03)),
            SurfaceMaterial.Concrete);
        var state = new MoveState { Position = Vec3d.Zero, Velocity = new Vec3d(0, 0, -6.2) };
        var config = new MovementConfig();
        var input = InputCmd.Create(1, 1, 0, 1, 0, 0, InputButtons.Sprint);
        Movement.Step(ref state, input, 0.5, config, MoveModifiers.Default, world);
        Assert.True(state.Position.Z >= -1.5 + 0.015 + config.Radius - 1e-8);
        Assert.InRange(System.Math.Abs(state.Velocity.Z), 0, 1e-8);
    }
}
