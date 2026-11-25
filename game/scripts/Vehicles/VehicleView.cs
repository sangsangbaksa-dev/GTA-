using Godot;
using System.Linq;
using Harbor.Core.Vehicles;
using Harbor.Game.Bridge;

namespace Harbor.Game.Vehicles;

public partial class VehicleView : Node3D
{
    [Export] public NodePath HostPath { get; set; } = new("../CoreHost");
    [Export] public PackedScene? Model { get; set; }

    private CoreHost _host = null!;
    private Node3D? _frontLeft;
    private Node3D? _frontRight;
    private Node3D[] _wheelRolls = [];

    public override void _Ready()
    {
        _host = GetNode<CoreHost>(HostPath);
        if (Model is null) return;
        var chassis = Model.Instantiate<Node3D>();
        // The asset's nose is +Z; world vehicle forward at zero yaw is -Z.
        chassis.RotateY(Mathf.Pi);
        AddChild(chassis);
        _frontLeft = FindWheel(chassis, "WheelFL");
        _frontRight = FindWheel(chassis, "WheelFR");
        _wheelRolls = new[] { "WheelFLRoll", "WheelFRRoll", "WheelRLRoll", "WheelRRRoll" }
            .Select(name => FindWheel(chassis, name)).OfType<Node3D>().ToArray();
    }

    public override void _Process(double delta)
    {
        if (_host.Vehicle is not { } car) return;
        GlobalPosition = CoreHost.ToGodot(car.Position);
        GlobalRotation = new Vector3(0, (float)car.Yaw, 0);
        var steer = new Vector3(0, -(float)car.Steering, 0);
        if (_frontLeft is not null) _frontLeft.Rotation = steer;
        if (_frontRight is not null) _frontRight.Rotation = steer;
        var roll = (float)System.Math.IEEERemainder(car.Distance / ArcadeVehicle.WheelRadius, 2 * System.Math.PI);
        foreach (var wheel in _wheelRolls) wheel.Rotation = new Vector3(roll, 0, 0);
    }

    private static Node3D? FindWheel(Node chassis, string name) => chassis.FindChild(name, true, false) as Node3D;
}
