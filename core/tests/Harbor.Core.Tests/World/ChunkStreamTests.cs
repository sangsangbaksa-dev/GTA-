using Harbor.Core.Data;
using Harbor.Core.Math;
using Harbor.Core.Ports;
using Harbor.Core.World;
using Xunit;

namespace Harbor.Core.Tests.World;

public class ChunkStreamTests
{
    private static ChunkStreamingConfig Harbor =>
        ChunkStreamingConfig.Load(new DiskFileSystem(Repo.Root), "data/world/harbor_streaming.json");

    [Fact]
    public void Harbor_Config_Uses_A_Cell_Smaller_Than_The_Slice()
    {
        var config = Harbor;
        Assert.Equal(64d, config.ChunkSizeMeters);
        Assert.Equal(2, config.LoadRadiusChunks);
        Assert.Equal(1, config.UnloadPaddingChunks);
        Assert.Equal(1.5, config.LookaheadSeconds);
        // 360m × 290m would sit inside a single 256m ring and never unload.
        Assert.True(config.ChunkSizeMeters < 290);
    }

    [Fact]
    public void Negative_Coordinates_Floor_Away_From_Zero()
    {
        Assert.Equal(new ChunkCoord(-4, -4), ChunkGrid.CoordOf(-215, -195, 64));
        Assert.Equal(new ChunkCoord(-3, 0), ChunkGrid.CoordOf(-192, 0, 64));
        Assert.Equal(new ChunkCoord(-1, 0), ChunkGrid.CoordOf(-64, 0, 64));
        Assert.Equal(new ChunkCoord(0, 0), ChunkGrid.CoordOf(0, 5, 64));
        Assert.Equal(new ChunkCoord(2, -3), ChunkGrid.CoordOf(139.4, -176.5, 64));
    }

    [Fact]
    public void Spawn_Loads_The_Quay_And_Leaves_The_Beacon_Out()
    {
        var config = Harbor;
        var resident = ChunkGrid.Select(new Vec3d(0, 0, 5), Vec3d.Zero, config);
        Assert.Equal(25, resident.Count);
        Assert.Contains(new ChunkCoord(0, 0), resident);
        Assert.Contains(new ChunkCoord(-1, -1), resident);
        Assert.DoesNotContain(new ChunkCoord(2, -3), resident);
        Assert.DoesNotContain(ChunkGrid.CoordOf(-208, -150, config.ChunkSizeMeters), resident);
        Assert.DoesNotContain(ChunkGrid.CoordOf(-215, -195, config.ChunkSizeMeters), resident);
    }

    [Fact]
    public void Velocity_Opens_A_Chunk_Beyond_The_Standing_Disk()
    {
        var config = Harbor;
        var standing = ChunkGrid.Select(new Vec3d(190, 0, 0), Vec3d.Zero, config);
        var moving = ChunkGrid.Select(new Vec3d(190, 0, 0), new Vec3d(16, 0, 0), config);
        Assert.DoesNotContain(new ChunkCoord(5, 0), standing);
        Assert.Contains(new ChunkCoord(5, 0), moving);
        Assert.Contains(new ChunkCoord(0, 0), moving);
    }

    [Fact]
    public void Padding_Keeps_A_Chunk_Until_It_Is_More_Than_One_Cell_Outside_The_Load_Disk()
    {
        var config = Harbor;
        var player = new Vec3d(190, 0, 0);
        var held = new ChunkCoord(5, 0);
        var dropped = new ChunkCoord(6, 0);
        var next = ChunkGrid.Select(player, Vec3d.Zero, config, new[] { held, dropped });
        Assert.Contains(held, next);
        Assert.DoesNotContain(dropped, next);
        Assert.DoesNotContain(dropped, ChunkGrid.Select(player, Vec3d.Zero, config));
    }

    [Fact]
    public void Rejects_A_Non_Positive_Chunk_Size()
    {
        var config = new ChunkStreamingConfig { ChunkSizeMeters = 0, LoadRadiusChunks = 1, UnloadPaddingChunks = 0, LookaheadSeconds = 0 };
        Assert.Throws<DataException>(() => config.Validate());
    }
}
