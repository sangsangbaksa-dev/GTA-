using System.Collections.Generic;
using Godot;
using Harbor.Game.Bridge;
using Harbor.Game.Player;

namespace Harbor.Game.Views;

/// <summary>Presents the authority's pedestrian snapshots with locally smoothed motion.</summary>
public partial class StreetPopulationView : Node3D
{
    private readonly Dictionary<int, Pedestrian> _people = new();
    private readonly Dictionary<string, Node3D> _cargo = new();
    private readonly HashSet<string> _visibleCargo = new();
    private readonly List<string> _missingCargo = new();
    private readonly HashSet<int> _seen = new();
    private readonly List<int> _expired = new();
    private CoreHost _host = null!;
    private PackedScene? _dockhand;
    private PackedScene? _civilian;
    private int _generation;

    public override void _Ready()
    {
        _host = GetNode<CoreHost>("../CoreHost");
        _dockhand = LoadOptional("res://assets/characters/chr_harbor_dockhand.glb");
        _civilian = LoadOptional("res://assets/characters/chr_npc_civilian.glb");
    }

    public override void _Process(double delta)
    {
        if (_generation != _host.RunGeneration)
        {
            _generation = _host.RunGeneration;
            _seen.Clear();
            RemoveMissing(_people);
            foreach (var parcel in _cargo.Values) parcel.QueueFree();
            _cargo.Clear();
        }

        var dt = (float)delta;
        var blend = 1f - Mathf.Exp(-12f * dt);
        var ambient = _host.Ambient;
        _seen.Clear();
        foreach (var pedestrian in ambient.Pedestrians)
        {
            _seen.Add(pedestrian.Id);
            var position = CoreHost.ToGodot(pedestrian.Position);
            var yaw = (float)pedestrian.Yaw;
            if (!_people.TryGetValue(pedestrian.Id, out var view))
            {
                view = AddPedestrian(pedestrian.Id, pedestrian.Appearance, position, yaw);
                _people.Add(pedestrian.Id, view);
            }

            MoveView(view.Root, position, yaw, blend, 16);
            var speed = (float)pedestrian.Speed;
            var stride = pedestrian.Fleeing ? 3.0f : 4.8f;
            view.Phase = Mathf.PosMod(view.Phase + speed * stride * dt, Mathf.Tau);
            view.Gait?.Update(view.Phase, speed, false, blend, dt,
                sprinting: pedestrian.Fleeing, hurried: pedestrian.Fleeing);
        }
        RemoveMissing(_people);

        _visibleCargo.Clear();
        foreach (var cargo in ambient.Cargo)
        {
            _visibleCargo.Add(cargo.Id);
            if (!_cargo.TryGetValue(cargo.Id, out var view))
            {
                view = MakeCargo(cargo.Id, CoreHost.ToGodot(cargo.Position));
                _cargo.Add(cargo.Id, view);
            }
            view.Visible = cargo.Available;
        }
        _missingCargo.Clear();
        foreach (var id in _cargo.Keys)
            if (!_visibleCargo.Contains(id)) _missingCargo.Add(id);
        foreach (var id in _missingCargo)
        {
            _cargo[id].QueueFree();
            _cargo.Remove(id);
        }
    }

    private Node3D MakeCargo(string id, Vector3 position)
    {
        var root = new Node3D { Name = $"Cargo_{id}", Position = position };
        AddChild(root);
        AddBox(new Vector3(0.64f, 0.41f, 0.45f), new Vector3(0, 0.205f, 0), new Color("#876446"));
        AddBox(new Vector3(0.68f, 0.08f, 0.49f), new Vector3(0, 0.42f, 0), new Color("#b78b52"));
        var metal = new Color("#435454");
        foreach (var side in new[] { -1, 1 })
        {
            AddBox(new Vector3(0.06f, 0.04f, 0.49f), new Vector3(side * 0.23f, 0.47f, 0), metal);
            AddBox(new Vector3(0.06f, 0.38f, 0.03f), new Vector3(side * 0.23f, 0.22f, 0.235f), metal);
        }
        return root;

        void AddBox(Vector3 size, Vector3 local, Color color) => root.AddChild(new MeshInstance3D
        {
            Position = local,
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.85f }
        });
    }

    private Pedestrian AddPedestrian(int id, int appearance, Vector3 position, float yaw)
    {
        var root = AddActorRoot("Pedestrian", id, position, yaw);
        var worker = appearance % 2 == 0 && _dockhand != null;
        var scene = worker ? _dockhand : _civilian ?? _dockhand;
        if (scene == null) return new Pedestrian(root, null, id);

        var model = scene.Instantiate<Node3D>();
        model.Rotation = new Vector3(0, Mathf.Pi, 0);
        var height = Mathf.Lerp(0.96f, 1.04f, Mathf.PosMod(id * 13, 7) / 6f);
        model.Scale = Vector3.One * height;
        root.AddChild(model);

        if (worker && appearance == 2)
            Recolor(model, "dockhand_hivis_vermilion", new Color("#c7aa50"));
        else if (!worker)
            Recolor(model, "hoodie_cobalt", appearance == 1 ? new Color("#755b72") : new Color("#637e81"));

        return new Pedestrian(root, RookGait.Create(model), id);
    }

    private Node3D AddActorRoot(string category, int id, Vector3 position, float yaw)
    {
        var root = new Node3D
        {
            Name = $"{category}_{id}",
            Position = position,
            Rotation = new Vector3(0, yaw, 0)
        };
        AddChild(root);
        return root;
    }

    private static void MoveView(Node3D root, Vector3 position, float yaw, float blend, float snapSquared)
    {
        if (root.Position.DistanceSquaredTo(position) > snapSquared) blend = 1;
        root.Position = root.Position.Lerp(position, blend);
        root.Rotation = new Vector3(0, Mathf.LerpAngle(root.Rotation.Y, yaw, blend), 0);
    }

    private static PackedScene? LoadOptional(string path) => ResourceLoader.Exists(path) ? GD.Load<PackedScene>(path) : null;

    private static void Recolor(Node3D root, string materialName, Color color)
    {
        if (root is MeshInstance3D mesh) RecolorMesh(mesh, materialName, color);
        foreach (var node in root.FindChildren("*", "MeshInstance3D", true, false))
            if (node is MeshInstance3D child) RecolorMesh(child, materialName, color);
    }

    private static void RecolorMesh(MeshInstance3D mesh, string materialName, Color color)
    {
        for (var i = 0; i < mesh.GetSurfaceOverrideMaterialCount(); i++)
        {
            if (mesh.GetActiveMaterial(i) is not StandardMaterial3D source || source.ResourceName != materialName)
                continue;

            var variant = (StandardMaterial3D)source.Duplicate();
            variant.AlbedoColor = color;
            mesh.SetSurfaceOverrideMaterial(i, variant);
        }
    }

    private void RemoveMissing<T>(Dictionary<int, T> views) where T : StreetActor
    {
        _expired.Clear();
        foreach (var id in views.Keys)
            if (!_seen.Contains(id)) _expired.Add(id);
        foreach (var id in _expired)
        {
            views[id].Root.QueueFree();
            views.Remove(id);
        }
    }

    private abstract class StreetActor(Node3D root)
    {
        public readonly Node3D Root = root;
    }

    private sealed class Pedestrian(Node3D root, RookGait? gait, int id) : StreetActor(root)
    {
        public readonly RookGait? Gait = gait;
        public float Phase = Mathf.PosMod(id * 1.7f, Mathf.Tau);
    }
}
