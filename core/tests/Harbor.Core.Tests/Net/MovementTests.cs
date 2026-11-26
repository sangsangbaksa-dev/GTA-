using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Sim;
using Xunit;

namespace Harbor.Core.Tests.Net;

public class MovementTests
{
    private static readonly MovementConfig Cfg = new();
    private const double Dt = 1.0 / 30;

    private static MoveState Run(int ticks, InputCmd cmd, StaticWorld? w = null, MoveModifiers? mods = null, MoveState s = default)
    {
        for (var i = 0; i < ticks; i++) Movement.Step(ref s, cmd, Dt, Cfg, mods ?? MoveModifiers.Default, w);
        return s;
    }

    [Fact]
    public void Walks_Forward_Along_Negative_Z_At_Walk_Speed()
    {
        var s = Run(60, InputCmd.Create(1, 0, 0, 1, 0, 0, InputButtons.None));
        Assert.Equal(Cfg.WalkSpeed, s.Velocity.Length, 2);
        Assert.True(s.Position.Z < -5);
        Assert.Equal(0, s.Position.X, 6);
    }

    [Fact]
    public void Sprint_Is_Faster_And_Blocked_While_Aiming_Or_Carrying()
    {
        var sprint = Run(60, InputCmd.Create(1, 0, 0, 1, 0, 0, InputButtons.Sprint));
        Assert.Equal(Cfg.SprintSpeed, sprint.Velocity.Length, 2);
        var aim = Run(60, InputCmd.Create(1, 0, 0, 1, 0, 0, InputButtons.Sprint | InputButtons.Aim));
        Assert.Equal(Cfg.WalkSpeed * Cfg.AimSpeedMultiplier, aim.Velocity.Length, 2);
        var carry = Run(60, InputCmd.Create(1, 0, 0, 1, 0, 0, InputButtons.Sprint), mods: Movement.ModifiersFor(ActorFlags.Carrying, Cfg));
        Assert.Equal(Cfg.WalkSpeed * Cfg.CarrySpeedMultiplier, carry.Velocity.Length, 2);
    }

    [Fact]
    public void Is_Deterministic()
    {
        var cmd = InputCmd.Create(1, 0, 0.3, 0.9, 0.7, 0, InputButtons.Sprint);
        Assert.Equal(Run(90, cmd).Position, Run(90, cmd).Position);
    }

    [Fact]
    public void Cover_Box_Blocks_And_Actor_Slides()
    {
        var w = new StaticWorld();
        w.Add(Aabb.FromCenter(new Vec3d(0, 0.5, -3), new Vec3d(10, 1.0, 0.6)), SurfaceMaterial.Concrete, "cover_low");
        // walk forward and slightly right into the barrier
        var s = Run(60, InputCmd.Create(1, 0, 0.3, 1, 0, 0, InputButtons.None), w);
        Assert.True(s.Position.Z > -3 + 0.3 + Cfg.Radius - 0.01, $"z={s.Position.Z}");
        Assert.True(s.Position.X > 1.0, "should slide sideways along the barrier");
    }

    [Fact]
    public void Jump_Rises_And_Lands_Without_Midair_Reset()
    {
        var jump = InputCmd.Create(1, 0, 0, 0, 0, 0, InputButtons.Jump);
        var idle = InputCmd.Create(2, 0, 0, 0, 0, 0, InputButtons.None);
        var s = Run(1, jump);
        Assert.True(s.Position.Y > 0);
        s = Run(8, idle, s: s);
        Assert.InRange(s.Position.Y, 0.5, 1.2);
        var next = Run(1, jump, s: s);
        Assert.True(next.Velocity.Y < s.Velocity.Y);
        s = Run(40, idle, s: next);
        Assert.Equal(0, s.Position.Y, 6);
        Assert.Equal(0, s.Velocity.Y, 6);
    }

    [Fact]
    public void Jump_Does_Not_Launch_While_Crouched_Or_Downed()
    {
        var crouch = InputCmd.Create(1, 0, 0, 0, 0, 0, InputButtons.Jump | InputButtons.Crouch);
        Assert.Equal(0, Run(1, crouch).Position.Y, 6);
        var jump = InputCmd.Create(1, 0, 0, 0, 0, 0, InputButtons.Jump);
        Assert.Equal(0, Run(1, jump, mods: Movement.ModifiersFor(ActorFlags.Downed, Cfg)).Position.Y, 6);
    }

    [Fact]
    public void Jump_Stops_At_Ceiling()
    {
        var world = new StaticWorld();
        world.Add(Aabb.FromCenter(new Vec3d(0, 2.35, 0), new Vec3d(5, 0.2, 5)), SurfaceMaterial.Concrete);
        var jump = InputCmd.Create(1, 0, 0, 0, 0, 0, InputButtons.Jump);
        var idle = InputCmd.Create(2, 0, 0, 0, 0, 0, InputButtons.None);
        var s = Run(1, jump, world);
        for (var i = 0; i < 30; i++)
        {
            s = Run(1, idle, world, s: s);
            Assert.InRange(s.Position.Y, 0, 0.450001);
        }
        Assert.Equal(0, s.Position.Y, 6);
    }

    [Fact]
    public void Dead_Cannot_Move()
    {
        var s = Run(30, InputCmd.Create(1, 0, 0, 1, 0, 0, InputButtons.None), mods: Movement.ModifiersFor(ActorFlags.Dead, Cfg));
        Assert.Equal(Vec3d.Zero, s.Position);
    }
}
