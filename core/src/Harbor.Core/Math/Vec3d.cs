namespace Harbor.Core.Math;

/// <summary>World-space position in double precision. The engine layer converts to
/// origin-relative float vectors (floating origin).
/// Convention (same as Godot): Y is up, forward at yaw 0 is -Z, yaw rotates counter-clockwise seen from above.</summary>
public readonly record struct Vec3d(double X, double Y, double Z)
{
    public static readonly Vec3d Zero = new(0, 0, 0);
    public static readonly Vec3d Up = new(0, 1, 0);
    public static Vec3d operator +(Vec3d a, Vec3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3d operator -(Vec3d a, Vec3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3d operator -(Vec3d a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3d operator *(Vec3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3d operator /(Vec3d a, double s) => new(a.X / s, a.Y / s, a.Z / s);
    public double Length => System.Math.Sqrt(X * X + Y * Y + Z * Z);
    public double LengthSquared => X * X + Y * Y + Z * Z;
    public double Dot(Vec3d o) => X * o.X + Y * o.Y + Z * o.Z;
    public Vec3d WithY(double y) => new(X, y, Z);
    public Vec3d Normalized() { var l = Length; return l > 1e-12 ? this / l : Zero; }
    public static Vec3d Lerp(Vec3d a, Vec3d b, double t) => a + (b - a) * t;
    public static double Distance(Vec3d a, Vec3d b) => (a - b).Length;

    /// <summary>Unit view direction for yaw/pitch in radians (yaw 0 → -Z, positive pitch looks up).</summary>
    public static Vec3d FromYawPitch(double yaw, double pitch)
    {
        var cp = System.Math.Cos(pitch);
        return new Vec3d(-System.Math.Sin(yaw) * cp, System.Math.Sin(pitch), -System.Math.Cos(yaw) * cp);
    }
}
