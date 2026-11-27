using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Net;

namespace Harbor.Core.Sim;

/// <summary>Tuning for on-foot movement (data/combat/movement.json).</summary>
public sealed record MovementConfig
{
    public double WalkSpeed { get; init; } = 3.4;
    public double SprintSpeed { get; init; } = 6.2;
    public double CrouchSpeed { get; init; } = 1.7;
    public double AimSpeedMultiplier { get; init; } = 0.6;
    public double Acceleration { get; init; } = 28.0;
    public double Deceleration { get; init; } = 22.0;
    public double Radius { get; init; } = 0.35;
    public double Height { get; init; } = 1.8;
    public double CarrySpeedMultiplier { get; init; } = 0.85;
    public double DownedSpeedMultiplier { get; init; } = 0.25;
}

/// <summary>Gameplay-imposed limits (carrying loot, downed...).</summary>
public readonly record struct MoveModifiers(double SpeedMultiplier, bool CanSprint, bool CanMove)
{
    public static readonly MoveModifiers Default = new(1.0, true, true);
}

/// <summary>Deterministic on-foot movement shared by the authoritative server and client prediction
/// (ADR 0004). Must depend only on its inputs — no clocks, no randomness, no engine calls.</summary>
public static class Movement
{
    public static MoveModifiers ModifiersFor(ActorFlags status, MovementConfig cfg)
    {
        if ((status & ActorFlags.Dead) != 0) return new MoveModifiers(0, false, false);
        if ((status & ActorFlags.Downed) != 0) return new MoveModifiers(cfg.DownedSpeedMultiplier, false, true);
        if ((status & ActorFlags.Carrying) != 0) return new MoveModifiers(cfg.CarrySpeedMultiplier, false, true);
        return MoveModifiers.Default;
    }

    public static void Step(ref MoveState s, in InputCmd cmd, double dt, MovementConfig cfg, MoveModifiers mods, StaticWorld? world)
    {
        s.Yaw = cmd.YawRadians;
        s.Pitch = cmd.PitchRadians;
        s.Crouched = cmd.Has(InputButtons.Crouch);
        s.Aiming = cmd.Has(InputButtons.Aim);

        var forward = Vec3d.FromYawPitch(s.Yaw, 0);
        var right = new Vec3d(-forward.Z, 0, forward.X);
        var wish = mods.CanMove ? right * cmd.Right + forward * cmd.Forward : Vec3d.Zero;
        if (wish.LengthSquared > 1) wish = wish.Normalized();

        s.Sprinting = mods.CanSprint && cmd.Has(InputButtons.Sprint) && !s.Crouched && !s.Aiming
                      && cmd.Forward > 0.5 && wish.LengthSquared > 0.25;
        var speed = s.Sprinting ? cfg.SprintSpeed : s.Crouched ? cfg.CrouchSpeed : cfg.WalkSpeed;
        if (s.Aiming) speed *= cfg.AimSpeedMultiplier;
        speed *= mods.SpeedMultiplier;

        var target = wish * speed;
        var delta = target - s.Velocity.WithY(0);
        var rate = wish.LengthSquared > 1e-6 ? cfg.Acceleration : cfg.Deceleration;
        var maxDelta = rate * dt;
        if (delta.Length > maxDelta) delta = delta.Normalized() * maxDelta;
        s.Velocity = (s.Velocity.WithY(0) + delta).WithY(0);

        s.Position += s.Velocity * dt;
        if (world == null) return;
        s.Position = world.AlignToGround(s.Position);
        ResolveCollisions(ref s, cfg, world);
        s.Position = world.AlignToGround(s.Position);
    }

    /// <summary>Pushes the actor's cylinder out of static boxes (up to 3 iterations) and removes
    /// velocity into the contact normal so the actor slides along cover.</summary>
    private static void ResolveCollisions(ref MoveState s, MovementConfig cfg, StaticWorld world)
    {
        var r = cfg.Radius;
        for (var iter = 0; iter < 3; iter++)
        {
            var moved = false;
            var probe = new Aabb(new Vec3d(s.Position.X - r, s.Position.Y + 0.05, s.Position.Z - r),
                                 new Vec3d(s.Position.X + r, s.Position.Y + cfg.Height, s.Position.Z + r));
            foreach (var box in world.Overlapping(probe))
            {
                var b = box.Box;
                if (b.Max.Y <= s.Position.Y + 0.3) continue; // low enough to ignore (curbs); steps handled by engine
                var cx = System.Math.Clamp(s.Position.X, b.Min.X, b.Max.X);
                var cz = System.Math.Clamp(s.Position.Z, b.Min.Z, b.Max.Z);
                var dx = s.Position.X - cx;
                var dz = s.Position.Z - cz;
                var d2 = dx * dx + dz * dz;
                if (d2 >= r * r) continue;
                Vec3d n;
                double push;
                if (d2 > 1e-12)
                {
                    var d = System.Math.Sqrt(d2);
                    n = new Vec3d(dx / d, 0, dz / d);
                    push = r - d;
                }
                else
                {
                    // centre inside the box: leave through the nearest face
                    var options = new[]
                    {
                        (s.Position.X - b.Min.X, new Vec3d(-1, 0, 0)), (b.Max.X - s.Position.X, new Vec3d(1, 0, 0)),
                        (s.Position.Z - b.Min.Z, new Vec3d(0, 0, -1)), (b.Max.Z - s.Position.Z, new Vec3d(0, 0, 1)),
                    };
                    var best = options.MinBy(o => o.Item1);
                    n = best.Item2;
                    push = best.Item1 + r;
                }
                s.Position += n * push;
                var vn = s.Velocity.Dot(n);
                if (vn < 0) s.Velocity -= n * vn;
                moved = true;
            }
            if (!moved) break;
        }
    }
}
