using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Harbor.Core.Math;
using Harbor.Core.Server;
using Harbor.Core.World;
using Harbor.Game.Adapters;
using Harbor.Game.Bridge;

namespace Harbor.Game.Streaming;

/// <summary>Loads and unloads harbor chunk scenes around the player. The shell scene keeps sky, water and
/// collision; this node only instances <c>scenes/world/chunks/chunk_X_Z.tscn</c>.</summary>
public partial class ChunkStreamer : Node3D
{
    private const string ConfigPath = "data/world/harbor_streaming.json";
    private const string LayoutPath = "data/world/harbor_front.json";
    private const int MaxConcurrentLoads = 2;

    private ChunkStreamingConfig _config = null!;
    private CoreHost? _host;
    private Vec3d _spawn;
    private readonly Dictionary<ChunkCoord, Node> _live = new();
    private readonly Dictionary<string, ChunkCoord> _inflight = new();
    private readonly HashSet<ChunkCoord> _absent = new();
    private readonly HashSet<ChunkCoord> _failed = new();
    private HashSet<ChunkCoord> _wanted = new();

    public int LoadedChunkCount => _live.Count;

    public override void _Ready()
    {
        ProcessPriority = 10;
        var files = new EmbeddedGameFiles();
        _config = ChunkStreamingConfig.Load(files, ConfigPath);
        _spawn = HarborLayout.Load(files, LayoutPath).SpawnPosition;
        _host = GetNodeOrNull<CoreHost>("../../CoreHost");
        foreach (var coord in Ordered(ChunkGrid.Select(_spawn, Vec3d.Zero, _config), ChunkGrid.CoordOf(_spawn.X, _spawn.Z, _config.ChunkSizeMeters)))
            TryAdoptSync(coord);
        GD.Print($"[Harbor] chunk stream size={_config.ChunkSizeMeters}m radius={_config.LoadRadiusChunks} " +
                 $"padding={_config.UnloadPaddingChunks} loaded={_live.Count}");
    }

    public override void _Process(double delta)
    {
        var position = _spawn;
        var velocity = Vec3d.Zero;
        if (_host != null && _host.IsNodeReady())
        {
            position = _host.RenderPosition;
            velocity = _host.IsDriving
                ? Vec3d.FromYawPitch(_host.Vehicle.Yaw, 0) * _host.Vehicle.Speed
                : _host.HasPlayer ? _host.Client.Predicted.Velocity : Vec3d.Zero;
        }

        var focus = ChunkGrid.CoordOf(position.X, position.Z, _config.ChunkSizeMeters);
        _wanted = ChunkGrid.Select(position, velocity, _config, _live.Keys);
        foreach (var coord in _live.Keys.Where(coord => !_wanted.Contains(coord)).ToArray())
            Unload(coord);
        PollLoads();
        foreach (var coord in Ordered(_wanted.Where(NeedsLoad), focus))
        {
            if (_inflight.Count >= MaxConcurrentLoads) break;
            Request(coord);
        }
    }

    private bool NeedsLoad(ChunkCoord coord) =>
        !_live.ContainsKey(coord) && !_absent.Contains(coord) && !_failed.Contains(coord)
        && !_inflight.ContainsValue(coord);

    private void TryAdoptSync(ChunkCoord coord)
    {
        var path = ScenePath(coord);
        if (!ResourceLoader.Exists(path))
        {
            _absent.Add(coord);
            return;
        }
        if (ResourceLoader.Load<PackedScene>(path, "", ResourceLoader.CacheMode.Ignore) is PackedScene scene)
            Adopt(coord, scene);
        else
            _failed.Add(coord);
    }

    private void Request(ChunkCoord coord)
    {
        var path = ScenePath(coord);
        if (!ResourceLoader.Exists(path))
        {
            _absent.Add(coord);
            return;
        }
        var error = ResourceLoader.LoadThreadedRequest(path, "", true, ResourceLoader.CacheMode.Ignore);
        if (error != Error.Ok)
        {
            _failed.Add(coord);
            GD.PushWarning($"[Harbor] chunk {coord.X},{coord.Z} request failed ({error})");
            return;
        }
        _inflight[path] = coord;
    }

    private void PollLoads()
    {
        foreach (var (path, coord) in _inflight.ToArray())
        {
            var status = ResourceLoader.LoadThreadedGetStatus(path);
            if (status == ResourceLoader.ThreadLoadStatus.InProgress) continue;
            _inflight.Remove(path);
            if (status != ResourceLoader.ThreadLoadStatus.Loaded)
            {
                _failed.Add(coord);
                GD.PushWarning($"[Harbor] chunk {coord.X},{coord.Z} failed to load ({status})");
                continue;
            }
            if (ResourceLoader.LoadThreadedGet(path) is PackedScene scene && _wanted.Contains(coord) && !_live.ContainsKey(coord))
                Adopt(coord, scene);
        }
    }

    private void Adopt(ChunkCoord coord, PackedScene scene)
    {
        var node = scene.Instantiate<Node>();
        node.Name = $"Chunk_{coord.X}_{coord.Z}";
        AddChild(node);
        _live[coord] = node;
    }

    private void Unload(ChunkCoord coord)
    {
        if (!_live.Remove(coord, out var node)) return;
        node.QueueFree();
    }

    private static string ScenePath(ChunkCoord coord) =>
        $"res://scenes/world/chunks/chunk_{coord.X}_{coord.Z}.tscn";

    private static IEnumerable<ChunkCoord> Ordered(IEnumerable<ChunkCoord> coords, ChunkCoord focus) =>
        coords.OrderBy(coord => ChunkGrid.Distance(coord, focus)).ThenBy(coord => coord.X).ThenBy(coord => coord.Z);
}
