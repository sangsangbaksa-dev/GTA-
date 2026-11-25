using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.World;
using Xunit;

namespace Harbor.Core.Tests.World;

public class MapTests
{
    private static MapData Map => Repo.Data.Map;

    [Fact]
    public void Quarter_Turn_Rotates_Footprint_Like_Godot()
    {
        var m = new MapData
        {
            Assets = new() { ["box"] = new AssetDef { Min = new double[] { 0, 0, 0 }, Size = new double[] { 4, 1, 2 } } },
            Placements = new() { new Placement("box", new double[] { 10, 0, 10 }, 1) },
        };
        var b = m.BoundsOf(m.Placements[0]);
        // +90° about Y maps local +X to world -Z and local +Z to world +X
        Assert.Equal(new Vec3d(10, 0, 6), b.Min);
        Assert.Equal(new Vec3d(12, 1, 10), b.Max);
    }

    [Fact]
    public void Generated_Map_Builds_Collision_With_Buildings_And_Cover()
    {
        var world = new StaticWorld();
        var ids = Map.BuildStatic(world);
        Assert.True(world.Count > 300, $"only {world.Count} boxes");
        Assert.Contains(world.All, b => b.Tag == "building");
        Assert.Contains(world.All, b => b.Tag == "explosive");
        Assert.Equal(ids.Count, world.Count);
    }

    [Fact]
    public void Spawn_And_Objective_Points_Are_Reachable()
    {
        var world = new StaticWorld();
        Map.BuildStatic(world);
        bool Blocked(Vec3d p) => world.Overlapping(new Aabb(p - new Vec3d(0.35, -0.1, 0.35), p + new Vec3d(0.35, 1.7, 0.35))).Any();
        Assert.False(Blocked(Map.SpawnPoint), "spawn inside geometry");
        foreach (var c in Repo.Data.Activities.Contracts.Values)
        foreach (var o in c.Objectives)
            Assert.False(Blocked(o.AtPos), $"{c.Id}/{o.Label} at {o.AtPos} is inside geometry");
        foreach (var t in Repo.Data.Travel.Points) Assert.False(Blocked(t.Pos), $"travel point {t.Id} blocked");
    }

    [Fact]
    public void Areas_Cover_The_Three_Districts()
    {
        Assert.Equal("old_mill", Map.AreaAt(new Vec3d(-420, 0, 310))?.District);
        Assert.Equal("harbor_front", Map.AreaAt(new Vec3d(260, 0, -180))?.District);
        Assert.Equal("glass_heights", Map.AreaAt(new Vec3d(880, 0, 640))?.District);
    }
}
