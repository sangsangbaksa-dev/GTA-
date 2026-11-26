using System.Collections.Generic;
using System.Linq;
using Godot;
using Harbor.Core.Server;
using Harbor.Core.Vehicles;
using Harbor.Game.Bridge;

namespace Harbor.Game.Vehicles;

public partial class VehicleView : Node3D
{
    [Export] public NodePath HostPath { get; set; } = new("../CoreHost");
    [Export] public PackedScene? Model { get; set; }

    private static readonly Color[] Paint = [new("#476d73"), new("#973f39"), new("#c4ad85"), new("#40527d")];
    private readonly Dictionary<int, CarView> _views = new();
    private readonly Dictionary<string, PackedScene?> _models = new();
    private readonly HashSet<int> _active = new();
    private CoreHost _host = null!;
    private int _generation;

    public override void _Ready() => _host = GetNode<CoreHost>(HostPath);

    public override void _Process(double delta)
    {
        if (_generation != _host.RunGeneration)
        {
            _generation = _host.RunGeneration;
            foreach (var item in _views.Values) item.Root.QueueFree();
            _views.Clear();
        }

        var blend = 1f - Mathf.Exp(-16f * (float)delta);
        _active.Clear();
        foreach (var snapshot in _host.Vehicles)
        {
            _active.Add(snapshot.Id);
            var local = _host.IsDriving && _host.Vehicle.Id == snapshot.Id;
            var car = local ? _host.Vehicle : snapshot;
            var position = CoreHost.ToGodot(local ? _host.RenderPosition : car.Position);
            var yaw = (float)car.Yaw;
            if (!_views.TryGetValue(car.Id, out var view))
            {
                view = CreateView(car, position, yaw);
                _views.Add(car.Id, view);
            }

            var amount = local || view.Root.Position.DistanceSquaredTo(position) > 100f ? 1f : blend;
            view.Root.Position = view.Root.Position.Lerp(position, amount);
            view.Root.Rotation = new Vector3(0, Mathf.LerpAngle(view.Root.Rotation.Y, yaw, amount), 0);
            var steer = new Vector3(0, -(float)car.Steering, 0);
            if (view.FrontLeft != null) view.FrontLeft.Rotation = steer;
            if (view.FrontRight != null) view.FrontRight.Rotation = steer;
            var roll = (float)System.Math.IEEERemainder((view.Distance += (car.Distance - view.Distance) * amount)
                / car.WheelRadius, 2 * System.Math.PI);
            foreach (var wheel in view.Rolls) wheel.Rotation = new Vector3(roll, 0, 0);
        }
        foreach (var id in _views.Keys.Where(id => !_active.Contains(id)).ToArray())
        {
            _views[id].Root.QueueFree();
            _views.Remove(id);
        }
    }

    private CarView CreateView(HarborVehicleState car, Vector3 position, float yaw)
    {
        var root = new Node3D { Name = $"Vehicle_{car.Id}", Position = position, Rotation = new Vector3(0, yaw, 0) };
        AddChild(root);
        var model = SceneFor(car.ModelId);
        if (model == null) return new CarView(root, null, null, [], car.Distance);

        var chassis = model.Instantiate<Node3D>();
        // Original models face +Z; authoritative vehicles face -Z at yaw zero.
        chassis.RotateY(Mathf.Pi);
        root.AddChild(chassis);
        if (car.Id != 1 && car.Appearance % 4 != 0)
            ApplyPaint(chassis, Paint[Mathf.PosMod(car.Appearance, Paint.Length)]);
        var rolls = new[] { "WheelFLRoll", "WheelFRRoll", "WheelRLRoll", "WheelRRRoll" }
            .Select(name => FindWheel(chassis, name)).OfType<Node3D>().ToArray();
        return new CarView(root, FindWheel(chassis, "WheelFL"), FindWheel(chassis, "WheelFR"), rolls, car.Distance);
    }

    private PackedScene? SceneFor(string modelId)
    {
        if (modelId == VehicleModel.StarterId) return Model;
        if (_models.TryGetValue(modelId, out var scene)) return scene;
        var path = $"res://assets/vehicles/veh_{modelId}.glb";
        _models[modelId] = scene = ResourceLoader.Exists(path) ? GD.Load<PackedScene>(path) : Model;
        return scene;
    }

    private static Node3D? FindWheel(Node chassis, string name) => chassis.FindChild(name, true, false) as Node3D;

    private static void ApplyPaint(Node3D chassis, Color color)
    {
        foreach (var child in chassis.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            for (var i = 0; i < mesh.GetSurfaceOverrideMaterialCount(); i++)
            {
                if (mesh.GetActiveMaterial(i) is not StandardMaterial3D source
                    || source.ResourceName is not ("pip_copper_orange" or "vehicle_paint")) continue;
                var variant = (StandardMaterial3D)source.Duplicate();
                variant.AlbedoColor = color;
                mesh.SetSurfaceOverrideMaterial(i, variant);
            }
        }
    }

    private sealed class CarView(Node3D root, Node3D? frontLeft, Node3D? frontRight, Node3D[] rolls, double distance)
    {
        public Node3D Root { get; } = root;
        public Node3D? FrontLeft { get; } = frontLeft;
        public Node3D? FrontRight { get; } = frontRight;
        public Node3D[] Rolls { get; } = rolls;
        public double Distance { get; set; } = distance;
    }
}
