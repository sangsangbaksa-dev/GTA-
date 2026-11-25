using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Sim;

namespace Harbor.Core.Combat;

public readonly record struct ActorHit(int ActorId, HitZone Zone, double Distance, double PenetrationScale, Vec3d Point);
public readonly record struct SurfaceImpact(Vec3d Point, Vec3d Normal, StaticBox Box, bool Penetrated);

public sealed record TraceResult(IReadOnlyList<SurfaceImpact> Impacts, ActorHit? Hit, Vec3d End);

/// <summary>Hitscan bullet trace against rewound actor poses and static cover, with material penetration.</summary>
public static class Ballistics
{
    public static TraceResult Trace(Vec3d origin, Vec3d dir, double range, int shooterId,
        IReadOnlyDictionary<int, PoseSample> targets, StaticWorld world, MaterialTable materials, WeaponClass weaponClass)
    {
        dir = dir.Normalized();
        ActorHit? best = null;
        foreach (var (id, pose) in targets)
        {
            if (id == shooterId || !pose.Alive) continue;
            if (Hitboxes.Raycast(origin, dir, range, pose, out var t, out var zone) && (best == null || t < best.Value.Distance))
                best = new ActorHit(id, zone, t, 1.0, origin + dir * t);
        }

        var impacts = new List<SurfaceImpact>();
        var limit = best?.Distance ?? range;
        var scale = 1.0;
        foreach (var h in world.RaycastAll(origin, dir, limit))
        {
            var thickness = h.Box.Box.ExitDistance(origin, dir, range) - h.Distance;
            var pass = materials.PassScale(h.Box.Material, weaponClass, thickness);
            impacts.Add(new SurfaceImpact(h.Point, h.Normal, h.Box, pass > 0));
            if (pass <= 0) return new TraceResult(impacts, null, h.Point);
            scale *= pass;
        }
        if (best is { } hit)
            return new TraceResult(impacts, hit with { PenetrationScale = scale }, hit.Point);
        return new TraceResult(impacts, null, origin + dir * range);
    }

    /// <summary>Shortest distance between a point and the shot segment (for suppression).</summary>
    public static double DistanceToSegment(Vec3d p, Vec3d a, Vec3d b)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared;
        var t = len2 < 1e-12 ? 0 : System.Math.Clamp((p - a).Dot(ab) / len2, 0, 1);
        return Vec3d.Distance(p, a + ab * t);
    }
}
