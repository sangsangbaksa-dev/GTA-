using Harbor.Core.Data;
using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Ports;
using Harbor.Core.Sim;
using M = System.Math;

namespace Harbor.Core.Vehicles;

public readonly record struct VehicleInput(double Throttle, double Turn, bool Brake);

public sealed record ArcadeVehicleTuning
{
    public double ForwardSpeed { get; init; } = 16;
    public double ReverseSpeed { get; init; } = 6;
    public double Acceleration { get; init; } = 6.8;
    public double ReverseAcceleration { get; init; } = 4.2;
    public double BrakeAcceleration { get; init; } = 17;
    public double CoastDeceleration { get; init; } = 2.4;
    public double SteeringRate { get; init; } = 3.8;
    public double MaxSteerDegrees { get; init; } = 32;
    public double HighSpeedSteerLoss { get; init; } = 0.6;
    public double CornerGrip { get; init; } = 1;

    private sealed record VehicleFile(ArcadeVehicleTuning Arcade);

    public static ArcadeVehicleTuning Load(IFileSystem fs, string path)
    {
        var tuning = Json.Read<VehicleFile>(fs, path).Arcade;
        Json.Require(tuning is not null, $"{path}: missing arcade settings");
        tuning!.Validate();
        return tuning;
    }

    internal void Validate()
    {
        foreach (var value in new[] { ForwardSpeed, ReverseSpeed, Acceleration, ReverseAcceleration,
                     BrakeAcceleration, CoastDeceleration, SteeringRate })
            Json.Require(value > 0 && double.IsFinite(value), "Vehicle speeds and rates must be positive and finite");
        Json.Require(MaxSteerDegrees is > 0 and < 90, "Vehicle steering angle must be between 0 and 90 degrees");
        Json.Require(HighSpeedSteerLoss is >= 0 and < 1 && CornerGrip is > 0 and <= 1,
            "Vehicle steering loss and corner grip must be valid proportions");
    }
}

/// <summary>Ground-following vehicle motion against the authoritative static level, in meters and radians.</summary>
public sealed class ArcadeVehicle
{
    public const double HalfWidth = 0.89;
    public const double HalfLength = 1.93;
    public const double RoofHeight = 1.54;
    public const double WheelRadius = 0.31;
    public const double Wheelbase = 2.36;
    private const double CurbClearance = 0.23;

    private readonly ArcadeVehicleTuning _tuning;

    public ArcadeVehicle(Vec3d position, double yaw = 0, ArcadeVehicleTuning? tuning = null, VehicleShape? shape = null)
    {
        Position = position;
        Yaw = yaw;
        _tuning = tuning ?? new ArcadeVehicleTuning();
        _tuning.Validate();
        Shape = shape ?? new VehicleShape();
    }

    public Vec3d Position { get; private set; }
    public double Yaw { get; private set; }
    public double Speed { get; private set; }
    public double Steering { get; private set; }
    public double Distance { get; private set; }
    public bool BlockedByObstacle { get; private set; }
    public VehicleShape Shape { get; }

    /// <summary>Apply a spawn pose or a snapshot from the authoritative simulation.</summary>
    public void Restore(Vec3d position, double yaw, double speed, double steering, double distance)
    {
        Position = position;
        Yaw = yaw;
        Speed = speed;
        Steering = steering;
        Distance = distance;
        BlockedByObstacle = false;
    }

    public void Step(VehicleInput input, double dt, StaticWorld world, Func<Vec3d, double, bool>? blockedByVehicle = null)
    {
        if (dt <= 0 || !double.IsFinite(dt)) return;
        BlockedByObstacle = false;
        var throttle = ClampInput(input.Throttle);
        var turn = ClampInput(input.Turn);
        var speedRatio = M.Clamp(M.Abs(Speed) / _tuning.ForwardSpeed, 0, 1);
        var steeringAtSpeed = 1 - _tuning.HighSpeedSteerLoss * speedRatio;
        var desiredSteering = turn * _tuning.MaxSteerDegrees * M.PI / 180 * steeringAtSpeed;
        Steering = Approach(Steering, desiredSteering, _tuning.SteeringRate * dt);

        if (input.Brake || (Speed != 0 && throttle != 0 && M.Sign(throttle) != M.Sign(Speed)))
            Speed = Approach(Speed, 0, _tuning.BrakeAcceleration * dt);
        else if (throttle == 0)
            Speed = Approach(Speed, 0, _tuning.CoastDeceleration * dt);
        else
        {
            var limit = throttle > 0 ? _tuning.ForwardSpeed : _tuning.ReverseSpeed;
            var acceleration = throttle > 0 ? _tuning.Acceleration : _tuning.ReverseAcceleration;
            Speed = Approach(Speed, throttle * limit, acceleration * dt);
        }
        Position = world.AlignToGround(Position);
        if (Speed == 0) return;

        // Small translation and heading increments prevent skipping thin geometry on longer ticks.
        var distance = Speed * dt;
        var headingDelta = -distance * M.Tan(Steering) / Shape.Wheelbase;
        var pieces = M.Max(1, (int)M.Ceiling(M.Max(M.Abs(distance) / 0.16, M.Abs(headingDelta) / 0.05)));
        for (var i = 0; i < pieces; i++)
        {
            var nextYaw = M.IEEERemainder(Yaw + headingDelta / pieces, 2 * M.PI);
            var headingMidpoint = Yaw + headingDelta / (2 * pieces) - Steering * (1 - _tuning.CornerGrip) * speedRatio;
            var movement = Vec3d.FromYawPitch(headingMidpoint, 0) * (distance / pieces);
            var nextPosition = world.AlignToGround(Position + movement);
            var moved = M.Abs(distance / pieces);
            if (IsBlocked(nextPosition, nextYaw))
            {
                // A glancing contact can keep the component parallel to an obstacle.
                var tangent = M.Abs(movement.X) > M.Abs(movement.Z)
                    ? new Vec3d(movement.X, 0, 0) : new Vec3d(0, 0, movement.Z);
                nextPosition = world.AlignToGround(Position + tangent);
                var forward = tangent * M.Sign(distance);
                var slideYaw = M.Atan2(-forward.X, -forward.Z);
                var tangentLength = tangent.Length;
                var canSlide = tangentLength >= moved * 0.7;
                if (canSlide && IsBlocked(nextPosition, slideYaw))
                {
                    slideYaw = Yaw;
                    canSlide = !IsBlocked(nextPosition, slideYaw);
                }
                if (!canSlide)
                {
                    Speed = 0;
                    BlockedByObstacle = true;
                    break;
                }
                Speed *= tangentLength / moved;
                moved = tangentLength;
                nextYaw = slideYaw;
            }
            Yaw = nextYaw;
            Position = nextPosition;
            Distance += moved * M.Sign(distance);
        }

        bool IsBlocked(Vec3d position, double yaw) => Blocked(world, position, yaw, Shape)
            || blockedByVehicle?.Invoke(position, yaw) == true;
    }

    internal static bool Blocked(StaticWorld world, Vec3d pivot, double yaw, VehicleShape shape)
    {
        var forward = Vec3d.FromYawPitch(yaw, 0);
        var right = new Vec3d(-forward.Z, 0, forward.X);
        var reachX = shape.HalfWidth * M.Abs(right.X) + shape.HalfLength * M.Abs(forward.X);
        var reachZ = shape.HalfWidth * M.Abs(right.Z) + shape.HalfLength * M.Abs(forward.Z);
        var bounds = new Aabb(pivot + new Vec3d(-reachX, CurbClearance, -reachZ),
            pivot + new Vec3d(reachX, shape.RoofHeight, reachZ));

        // Broad-phase supplies the two world-axis tests; these two projections test the vehicle axes.
        foreach (var box in world.Overlapping(bounds))
        {
            var offset = (box.Box.Min + box.Box.Max) / 2 - pivot;
            var extent = (box.Box.Max - box.Box.Min) / 2;
            if (Separated(offset, extent, forward, shape.HalfLength) || Separated(offset, extent, right, shape.HalfWidth)) continue;
            return true;
        }
        return false;
    }

    internal static bool Overlaps(Vec3d a, double aYaw, VehicleShape aShape, Vec3d b, double bYaw, VehicleShape bShape)
    {
        if (a.Y + aShape.RoofHeight <= b.Y || b.Y + bShape.RoofHeight <= a.Y) return false;
        var offset = b - a;
        var radius = aShape.HalfLength + bShape.HalfLength + aShape.HalfWidth + bShape.HalfWidth;
        if (offset.WithY(0).LengthSquared > radius * radius) return false;

        var aForward = Vec3d.FromYawPitch(aYaw, 0);
        var aRight = new Vec3d(-aForward.Z, 0, aForward.X);
        var bForward = Vec3d.FromYawPitch(bYaw, 0);
        var bRight = new Vec3d(-bForward.Z, 0, bForward.X);
        return Intersects(aForward) && Intersects(aRight) && Intersects(bForward) && Intersects(bRight);

        bool Intersects(Vec3d axis) =>
            M.Abs(axis.Dot(offset)) < M.Abs(axis.Dot(aForward)) * aShape.HalfLength + M.Abs(axis.Dot(aRight)) * aShape.HalfWidth
                                + M.Abs(axis.Dot(bForward)) * bShape.HalfLength + M.Abs(axis.Dot(bRight)) * bShape.HalfWidth;
    }

    internal static void ResolvePedestrianContact(ref MoveState move, MovementConfig config, Vec3d pivot, double yaw, VehicleShape shape)
    {
        var relative = move.Position - pivot;
        var radius = config.Radius + 0.015;
        var reach = shape.HalfLength + shape.HalfWidth + radius;
        if (relative.Y >= shape.RoofHeight || relative.Y + config.Height <= 0.12
            || relative.WithY(0).LengthSquared >= reach * reach) return;

        var forward = Vec3d.FromYawPitch(yaw, 0);
        var right = new Vec3d(-forward.Z, 0, forward.X);
        var x = relative.Dot(right);
        var z = relative.Dot(forward);
        var dx = x - M.Clamp(x, -shape.HalfWidth, shape.HalfWidth);
        var dz = z - M.Clamp(z, -shape.HalfLength, shape.HalfLength);
        var squared = dx * dx + dz * dz;
        if (squared >= radius * radius) return;

        var edgeX = shape.HalfWidth - M.Abs(x);
        var edgeZ = shape.HalfLength - M.Abs(z);
        var distance = M.Sqrt(squared);
        var outward = distance > 1e-8 ? (right * dx + forward * dz) / distance
            : edgeX < edgeZ ? right * (x < 0 ? -1 : 1) : forward * (z < 0 ? -1 : 1);
        var separation = distance > 1e-8 ? radius - distance : radius + M.Min(edgeX, edgeZ);
        move.Position += outward * separation;
        var inwardSpeed = move.Velocity.Dot(outward);
        if (inwardSpeed < 0) move.Velocity -= outward * inwardSpeed;
    }

    internal static double CameraObstruction(Vec3d origin, Vec3d direction, double distance, Vec3d pivot, double yaw, VehicleShape shape)
    {
        var offset = origin - pivot;
        var reach = distance + shape.HalfLength + shape.HalfWidth;
        if (offset.WithY(0).LengthSquared > reach * reach) return distance;

        var forward = Vec3d.FromYawPitch(yaw, 0);
        var right = new Vec3d(-forward.Z, 0, forward.X);
        var localOrigin = new Vec3d(offset.Dot(right), offset.Y, offset.Dot(forward));
        var localDirection = new Vec3d(direction.Dot(right), direction.Y, direction.Dot(forward));
        var body = new Aabb(new Vec3d(-shape.HalfWidth, 0.12, -shape.HalfLength),
            new Vec3d(shape.HalfWidth, shape.RoofHeight, shape.HalfLength));
        return body.Raycast(localOrigin, localDirection, distance, out var hit, out _) && hit > 0.001 ? hit : distance;
    }

    private static bool Separated(Vec3d offset, Vec3d extent, Vec3d axis, double halfSize) =>
        M.Abs(offset.Dot(axis)) >= halfSize + extent.X * M.Abs(axis.X) + extent.Z * M.Abs(axis.Z);

    private static double ClampInput(double value) => double.IsFinite(value) ? M.Clamp(value, -1, 1) : 0;

    private static double Approach(double current, double target, double amount) =>
        current + M.Clamp(target - current, -amount, amount);
}
