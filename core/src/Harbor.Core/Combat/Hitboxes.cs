using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Sim;

namespace Harbor.Core.Combat;

public enum HitZone : byte { Head, Torso, Limbs }

/// <summary>Server hit volumes for a humanoid pose: head sphere, torso capsule, legs capsule
/// (arms are folded into a slightly wider torso). Sized to the Blender character rigs (1.8 m).</summary>
public static class Hitboxes
{
    private readonly record struct Shape(HitZone Zone, double Y0, double Y1, double Radius);

    private static readonly Shape[] Standing =
    {
        new(HitZone.Head, 1.64, 1.64, 0.13),
        new(HitZone.Torso, 1.00, 1.30, 0.24),
        new(HitZone.Limbs, 0.12, 0.86, 0.18),
    };

    private static readonly Shape[] Crouched =
    {
        new(HitZone.Head, 1.14, 1.14, 0.13),
        new(HitZone.Torso, 0.55, 0.76, 0.24),
        new(HitZone.Limbs, 0.12, 0.40, 0.22),
    };

    /// <summary>Nearest intersection of a unit-direction ray with the pose's hit volumes.</summary>
    public static bool Raycast(Vec3d origin, Vec3d dir, double maxDist, PoseSample pose, out double t, out HitZone zone)
    {
        t = double.MaxValue;
        zone = HitZone.Torso;
        if (!double.IsFinite(maxDist) || maxDist < 0 || !double.IsFinite(dir.LengthSquared)
            || dir.LengthSquared < 1e-12) return false;
        dir = dir.Normalized();

        // The feet are not the center of the hit volume. A full pose AABB is
        // conservative even for steep rays and for rays starting inside a body.
        var bounds = new Aabb(pose.Position + new Vec3d(-0.24, -0.10, -0.24),
            pose.Position + new Vec3d(0.24, pose.Crouched ? 1.28 : 1.78, 0.24));
        if (!bounds.Raycast(origin, dir, maxDist, out _, out _)) return false;

        var found = false;
        foreach (var s in pose.Crouched ? Crouched : Standing)
        {
            var a = pose.Position + new Vec3d(0, s.Y0, 0);
            var b = pose.Position + new Vec3d(0, s.Y1, 0);
            var ti = Capsule(origin, dir, a, b, s.Radius);
            if (ti >= 0 && ti <= maxDist && ti < t) { t = ti; zone = s.Zone; found = true; }
        }
        return found;
    }

    /// <summary>Ray vs capsule (segment a-b, radius r); returns entry distance or -1. Degenerate segment = sphere.</summary>
    public static double Capsule(Vec3d ro, Vec3d rd, Vec3d pa, Vec3d pb, double ra)
    {
        var ba = pb - pa;
        var oa = ro - pa;
        var baba = ba.Dot(ba);
        if (baba < 1e-12) return Sphere(ro, rd, pa, ra);
        var nearest = pa + ba * System.Math.Clamp(ba.Dot(oa) / baba, 0, 1);
        if ((ro - nearest).LengthSquared <= ra * ra) return 0;
        var bard = ba.Dot(rd);
        var baoa = ba.Dot(oa);
        var rdoa = rd.Dot(oa);
        var oaoa = oa.Dot(oa);
        var a = baba - bard * bard;
        var b = baba * rdoa - baoa * bard;
        var c = baba * oaoa - baoa * baoa - ra * ra * baba;
        if (System.Math.Abs(a) > 1e-12)
        {
            var h = b * b - a * c;
            if (h < 0) return -1;
            var t = (-b - System.Math.Sqrt(h)) / a;
            var y = baoa + t * bard;
            if (y > 0 && y < baba) return t >= 0 ? t : -1;
        }
        // caps
        var t1 = Sphere(ro, rd, pa, ra);
        var t2 = Sphere(ro, rd, pb, ra);
        if (t1 < 0) return t2;
        if (t2 < 0) return t1;
        return System.Math.Min(t1, t2);
    }

    public static double Sphere(Vec3d ro, Vec3d rd, Vec3d center, double r)
    {
        var oc = ro - center;
        var b = oc.Dot(rd);
        var c = oc.Dot(oc) - r * r;
        if (c <= 0) return 0;
        var h = b * b - c;
        if (h < 0) return -1;
        var t = -b - System.Math.Sqrt(h);
        return t >= 0 ? t : -1;
    }
}
