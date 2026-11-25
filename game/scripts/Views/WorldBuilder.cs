using System.Collections.Generic;
using Godot;
using Harbor.Core.Client;
using Harbor.Core.World;
using Harbor.Game.Bridge;

namespace Harbor.Game.Views;

/// <summary>Builds the visible city from data/world/map.json — the same file the server builds collision from,
/// so what you see is what blocks bullets (ADR 0008). Also owns sky, sun and ground.</summary>
public partial class WorldBuilder(GameSession session) : Node3D
{
    private readonly Dictionary<string, PackedScene?> _scenes = new();
    private readonly Dictionary<int, Node3D> _byBox = new();

    public override void _Ready()
    {
        BuildEnvironment();
        var map = session.Data.Map;
        BuildGround(map);
        var boxToPlacement = new Dictionary<int, int>();
        foreach (var (pi, box) in session.PlacementBoxes) boxToPlacement[pi] = box;
        for (var i = 0; i < map.Placements.Count; i++)
        {
            var p = map.Placements[i];
            var node = Instance(p.Asset);
            if (node == null) continue;
            node.Position = new Vector3((float)p.Pos[0], (float)p.Pos[1], (float)p.Pos[2]);
            node.Rotation = new Vector3(0, p.Rot * Mathf.Pi / 2, 0);
            AddChild(node);
            if (boxToPlacement.TryGetValue(i, out var boxId)) _byBox[boxId] = node;
        }
        session.FxRaised += OnFx;
    }

    public override void _ExitTree() => session.FxRaised -= OnFx;

    private void OnFx(ClientFx fx)
    {
        if (fx is ExplosionFx ex && _byBox.Remove(ex.BoxId, out var node)) node.QueueFree();
    }

    private Node3D? Instance(string asset)
    {
        if (!_scenes.TryGetValue(asset, out var scene))
        {
            var dir = asset.StartsWith("bldg_") ? "buildings" : "props";
            var path = $"res://assets/{dir}/{asset}.glb";
            scene = ResourceLoader.Exists(path) ? GD.Load<PackedScene>(path) : null;
            _scenes[asset] = scene;
        }
        if (scene != null) return scene.Instantiate<Node3D>();
        // missing asset: grey box with the same footprint so collision is still visible
        var a = session.Data.Map.Assets[asset];
        var mi = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3((float)a.Size[0], (float)a.Size[1], (float)a.Size[2]) },
            Position = new Vector3((float)(a.Min[0] + a.Size[0] / 2), (float)(a.Min[1] + a.Size[1] / 2), (float)(a.Min[2] + a.Size[2] / 2)),
        };
        var root = new Node3D();
        root.AddChild(mi);
        return root;
    }

    private void BuildEnvironment()
    {
        var sky = new Sky
        {
            SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = new Color(0.32f, 0.47f, 0.68f),
                SkyHorizonColor = new Color(0.78f, 0.72f, 0.62f),
                GroundHorizonColor = new Color(0.55f, 0.5f, 0.45f),
                GroundBottomColor = new Color(0.2f, 0.2f, 0.22f),
                SunAngleMax = 30,
            },
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.35f,
            AmbientLightSkyContribution = 0.6f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            SsaoEnabled = true,
            FogEnabled = true,
            FogLightColor = new Color(0.72f, 0.7f, 0.66f),
            FogDensity = 0.0009f,
            FogSkyAffect = 0.3f,
            GlowEnabled = true,
            GlowIntensity = 0.2f,
            TonemapExposure = 0.85f,
        };
        AddChild(new WorldEnvironment { Environment = env });
        var sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-48, -35, 0),
            LightEnergy = 1.0f,
            LightColor = new Color(1f, 0.95f, 0.86f),
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 160,
        };
        AddChild(sun);
    }

    private static StandardMaterial3D Mat(Color c, float rough = 0.9f, float noiseScale = 0)
    {
        var m = new StandardMaterial3D { AlbedoColor = c, Roughness = rough };
        if (noiseScale > 0)
        {
            // cheap large-scale variation so open ground doesn't read as a flat colour
            m.AlbedoTexture = new NoiseTexture2D
            {
                Width = 512, Height = 512, Seamless = true,
                Noise = new FastNoiseLite { Frequency = 0.02f, FractalOctaves = 4 },
                ColorRamp = new Gradient { Colors = new[] { new Color(0.78f, 0.78f, 0.78f), new Color(1.1f, 1.1f, 1.1f) }, Offsets = new[] { 0f, 1f } },
            };
            m.Uv1Triplanar = true;
            m.Uv1Scale = new Vector3(noiseScale, noiseScale, noiseScale);
        }
        return m;
    }

    private void BuildGround(MapData map)
    {
        var b = map.Bounds;
        var size = new Vector2((float)(b[2] - b[0]) + 400, (float)(b[3] - b[1]) + 400);
        var center = new Vector3((float)(b[0] + b[2]) / 2, 0, (float)(b[1] + b[3]) / 2);
        AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = size }, Position = center + new Vector3(0, -0.02f, 0), MaterialOverride = Mat(new Color(0.26f, 0.3f, 0.22f), 0.95f, 0.02f) });

        var paved = Mat(new Color(0.3f, 0.3f, 0.29f), 0.9f, 0.06f);
        foreach (var a in map.Areas)
        {
            var r = a.Rect;
            AddChild(new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2((float)(r[2] - r[0]), (float)(r[3] - r[1])) },
                Position = new Vector3((float)(r[0] + r[2]) / 2, -0.01f, (float)(r[1] + r[3]) / 2),
                MaterialOverride = paved,
            });
        }

        var asphalt = Mat(new Color(0.12f, 0.12f, 0.13f), 0.75f, 0.15f);
        var line = Mat(new Color(0.85f, 0.78f, 0.45f), 0.6f);
        foreach (var rd in map.Roads)
        {
            var from = new Vector3((float)rd.From[0], 0, (float)rd.From[1]);
            var to = new Vector3((float)rd.To[0], 0, (float)rd.To[1]);
            var len = from.DistanceTo(to);
            var mid = (from + to) / 2;
            var yaw = Mathf.Atan2(to.X - from.X, to.Z - from.Z);
            AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3((float)rd.Width, 0.04f, len + (float)rd.Width) },
                Position = mid + new Vector3(0, 0.0f, 0), Rotation = new Vector3(0, yaw, 0), MaterialOverride = asphalt,
            });
            AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.18f, 0.045f, len) },
                Position = mid, Rotation = new Vector3(0, yaw, 0), MaterialOverride = line,
            });
        }

        if (!double.IsNegativeInfinity(map.WaterZ))
        {
            var w = (float)(b[2] - b[0]) + 400;
            var depth = 600f;
            var water = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.12f, 0.26f, 0.33f, 0.92f),
                Metallic = 0.2f, Roughness = 0.08f,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            };
            AddChild(new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(w, depth) },
                Position = new Vector3(center.X, 0.03f, (float)map.WaterZ - depth / 2),
                MaterialOverride = water,
            });
        }
    }
}
