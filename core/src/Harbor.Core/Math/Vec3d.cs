namespace Harbor.Core.Math;

/// <summary>World-space position in double precision. The engine layer converts to
/// origin-relative float vectors (floating origin).</summary>
public readonly record struct Vec3d(double X, double Y, double Z)
{
    public static readonly Vec3d Zero = new(0, 0, 0);
    public static Vec3d operator +(Vec3d a, Vec3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3d operator -(Vec3d a, Vec3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3d operator *(Vec3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public double Length => System.Math.Sqrt(X * X + Y * Y + Z * Z);
}
