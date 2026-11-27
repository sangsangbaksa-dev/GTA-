using Harbor.Core.Math;
using Harbor.Core.Ports;

namespace Harbor.Core.Geometry;

/// <summary>Surface material for bullet penetration (data/combat/materials.json).</summary>
public enum SurfaceMaterial : byte { Concrete, Metal, ThinMetal, Wood, Glass, Earth }

public readonly record struct Aabb(Vec3d Min, Vec3d Max)
{
    public static Aabb FromCenter(Vec3d center, Vec3d size) => new(center - size / 2, center + size / 2);

    public bool Intersects(Aabb o) =>
        Min.X < o.Max.X && Max.X > o.Min.X && Min.Y < o.Max.Y && Max.Y > o.Min.Y && Min.Z < o.Max.Z && Max.Z > o.Min.Z;

    /// <summary>Slab test. Returns entry distance along a unit direction and the face normal.</summary>
    public bool Raycast(Vec3d origin, Vec3d dir, double maxDist, out double t, out Vec3d normal)
    {
        double tmin = 0, tmax = maxDist;
        normal = Vec3d.Zero;
        var n = Vec3d.Zero;
        if (!Slab(origin.X, dir.X, Min.X, Max.X, new Vec3d(-1, 0, 0), ref tmin, ref tmax, ref n) ||
            !Slab(origin.Y, dir.Y, Min.Y, Max.Y, new Vec3d(0, -1, 0), ref tmin, ref tmax, ref n) ||
            !Slab(origin.Z, dir.Z, Min.Z, Max.Z, new Vec3d(0, 0, -1), ref tmin, ref tmax, ref n))
        {
            t = 0;
            return false;
        }
        t = tmin;
        normal = n;
        return true;
    }

    /// <summary>Distance the ray travels inside the box (for penetration thickness).</summary>
    public double ExitDistance(Vec3d origin, Vec3d dir, double maxDist)
    {
        double tmin = 0, tmax = maxDist;
        var n = Vec3d.Zero;
        Slab(origin.X, dir.X, Min.X, Max.X, Vec3d.Zero, ref tmin, ref tmax, ref n);
        Slab(origin.Y, dir.Y, Min.Y, Max.Y, Vec3d.Zero, ref tmin, ref tmax, ref n);
        Slab(origin.Z, dir.Z, Min.Z, Max.Z, Vec3d.Zero, ref tmin, ref tmax, ref n);
        return tmax;
    }

    private static bool Slab(double o, double d, double min, double max, Vec3d axisNeg, ref double tmin, ref double tmax, ref Vec3d n)
    {
        if (System.Math.Abs(d) < 1e-12) return o >= min && o <= max;
        var t1 = (min - o) / d;
        var t2 = (max - o) / d;
        var nn = axisNeg;
        if (t1 > t2) { (t1, t2) = (t2, t1); nn = -axisNeg; }
        if (t1 > tmin) { tmin = t1; n = nn; }
        if (t2 < tmax) tmax = t2;
        return tmin <= tmax;
    }
}

public readonly record struct StaticBox(int Id, Aabb Box, SurfaceMaterial Material, string Tag);

public readonly record struct RayHit(double Distance, Vec3d Point, Vec3d Normal, StaticBox Box);

/// <summary>Server-side level collision (cover, walls, props) built from data — the dedicated server
/// has no engine physics. Uniform spatial hash broadphase. Also implements the <see cref="IPhysicsQuery"/> port.</summary>
public sealed class StaticWorld(double cellSize = 8.0, Func<Vec3d, double>? terrainElevation = null) : IPhysicsQuery
{
    private readonly Dictionary<int, StaticBox> _boxes = new();
    private readonly Dictionary<(int, int), List<int>> _cells = new();
    private int _nextId = 1;

    public int Count => _boxes.Count;

    public Vec3d AlignToGround(Vec3d point) => terrainElevation is null ? point : point.WithY(terrainElevation(point));

    public int Add(Aabb box, SurfaceMaterial material, string tag = "")
    {
        var id = _nextId++;
        _boxes[id] = new StaticBox(id, box, material, tag);
        foreach (var c in CellsOf(box))
        {
            if (!_cells.TryGetValue(c, out var list)) _cells[c] = list = new List<int>();
            list.Add(id);
        }
        return id;
    }

    /// <summary>Removes a box (destroyed cover). Returns false if unknown.</summary>
    public bool Remove(int id)
    {
        if (!_boxes.Remove(id, out var b)) return false;
        foreach (var c in CellsOf(b.Box)) _cells[c].Remove(id);
        return true;
    }

    public bool TryGet(int id, out StaticBox box) => _boxes.TryGetValue(id, out box);
    public IEnumerable<StaticBox> All => _boxes.Values;

    public IEnumerable<StaticBox> Overlapping(Aabb query)
    {
        var seen = new HashSet<int>();
        foreach (var c in CellsOf(query))
        {
            if (!_cells.TryGetValue(c, out var list)) continue;
            foreach (var id in list)
                if (seen.Add(id) && _boxes[id].Box.Intersects(query)) yield return _boxes[id];
        }
    }

    /// <summary>All boxes hit along the ray, nearest first.</summary>
    public List<RayHit> RaycastAll(Vec3d origin, Vec3d dir, double maxDist)
    {
        dir = dir.Normalized();
        var hits = new List<RayHit>();
        var seen = new HashSet<int>();
        var step = cellSize * 0.5;
        for (double s = 0; s <= maxDist + step; s += step)
        {
            var p = origin + dir * System.Math.Min(s, maxDist);
            var key = Cell(p.X, p.Z);
            for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (!_cells.TryGetValue((key.Item1 + dx, key.Item2 + dz), out var list)) continue;
                foreach (var id in list)
                {
                    if (!seen.Add(id)) continue;
                    var b = _boxes[id];
                    if (b.Box.Raycast(origin, dir, maxDist, out var t, out var n))
                        hits.Add(new RayHit(t, origin + dir * t, n, b));
                }
            }
        }
        hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return hits;
    }

    public bool Raycast(Vec3d origin, Vec3d dir, double maxDist, out RayHit hit)
    {
        var all = RaycastAll(origin, dir, maxDist);
        hit = all.Count > 0 ? all[0] : default;
        return all.Count > 0;
    }

    public double CameraObstruction(Vec3d origin, Vec3d direction, double distance)
    {
        var available = Raycast(origin, direction, distance, out var hit) ? hit.Distance : distance;
        if (terrainElevation is null) return available;
        var ray = direction.Normalized();
        var steps = (int)System.Math.Ceiling(available / 0.25);
        for (var index = 1; index <= steps; index++)
        {
            var sample = available * index / steps;
            var point = origin + ray * sample;
            var elevation = terrainElevation(point);
            if (elevation > 0 && point.Y < elevation + 0.08)
                return available * (index - 1) / steps;
        }
        return available;
    }

    bool IPhysicsQuery.Raycast(Vec3d from, Vec3d to, out Vec3d hit)
    {
        var d = to - from;
        if (Raycast(from, d, d.Length, out var h)) { hit = h.Point; return true; }
        hit = to;
        return false;
    }

    private (int, int) Cell(double x, double z) => ((int)System.Math.Floor(x / cellSize), (int)System.Math.Floor(z / cellSize));

    private IEnumerable<(int, int)> CellsOf(Aabb b)
    {
        var (x0, z0) = Cell(b.Min.X, b.Min.Z);
        var (x1, z1) = Cell(b.Max.X, b.Max.Z);
        for (var x = x0; x <= x1; x++)
        for (var z = z0; z <= z1; z++)
            yield return (x, z);
    }
}
