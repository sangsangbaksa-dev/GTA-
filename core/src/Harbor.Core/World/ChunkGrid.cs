using Harbor.Core.Data;
using Harbor.Core.Math;
using Harbor.Core.Ports;

namespace Harbor.Core.World;

/// <summary>Horizontal cell on the streaming grid. Y is up, so a chunk is addressed by X and Z.</summary>
public readonly record struct ChunkCoord(int X, int Z);

/// <summary>How far around the player the harbor scene stays loaded. Values live in data, not code.</summary>
public sealed record ChunkStreamingConfig
{
    public double ChunkSizeMeters { get; init; }
    public int LoadRadiusChunks { get; init; }
    public int UnloadPaddingChunks { get; init; }
    public double LookaheadSeconds { get; init; }

    public void Validate()
    {
        Json.Require(ChunkSizeMeters > 0 && double.IsFinite(ChunkSizeMeters), "streaming: chunkSizeMeters must be positive");
        Json.Require(LoadRadiusChunks >= 0, "streaming: loadRadiusChunks must be >= 0");
        Json.Require(UnloadPaddingChunks >= 0, "streaming: unloadPaddingChunks must be >= 0");
        Json.Require(LookaheadSeconds >= 0 && double.IsFinite(LookaheadSeconds), "streaming: lookaheadSeconds must be >= 0");
    }

    public static ChunkStreamingConfig Load(IFileSystem files, string path)
    {
        var config = Json.Read<ChunkStreamingConfig>(files, path);
        config.Validate();
        return config;
    }
}

/// <summary>Chooses which harbor chunks must be resident from the player position and velocity.</summary>
public static class ChunkGrid
{
    public static ChunkCoord CoordOf(double x, double z, double chunkSize) =>
        new((int)System.Math.Floor(x / chunkSize), (int)System.Math.Floor(z / chunkSize));

    public static int Distance(ChunkCoord a, ChunkCoord b) =>
        System.Math.Max(System.Math.Abs(a.X - b.X), System.Math.Abs(a.Z - b.Z));

    /// <summary>
    /// Chebyshev disk of <see cref="ChunkStreamingConfig.LoadRadiusChunks"/> around the player and around
    /// a point <see cref="ChunkStreamingConfig.LookaheadSeconds"/> along the velocity. Chunks already
    /// resident stay until they leave that disk plus <see cref="ChunkStreamingConfig.UnloadPaddingChunks"/>,
    /// so crossing a border does not unload and reload the same cell.
    /// </summary>
    public static HashSet<ChunkCoord> Select(Vec3d position, Vec3d velocity, ChunkStreamingConfig config,
        IEnumerable<ChunkCoord>? resident = null)
    {
        var player = CoordOf(position.X, position.Z, config.ChunkSizeMeters);
        var ahead = position + velocity * config.LookaheadSeconds;
        var focus = CoordOf(ahead.X, ahead.Z, config.ChunkSizeMeters);
        var keep = config.LoadRadiusChunks + config.UnloadPaddingChunks;
        var result = new HashSet<ChunkCoord>();
        AddDisk(result, player, config.LoadRadiusChunks);
        AddDisk(result, focus, config.LoadRadiusChunks);
        if (resident == null) return result;
        foreach (var chunk in resident)
            if (Distance(chunk, player) <= keep || Distance(chunk, focus) <= keep)
                result.Add(chunk);
        return result;
    }

    private static void AddDisk(HashSet<ChunkCoord> into, ChunkCoord center, int radius)
    {
        for (var x = center.X - radius; x <= center.X + radius; x++)
            for (var z = center.Z - radius; z <= center.Z + radius; z++)
                into.Add(new ChunkCoord(x, z));
    }
}
