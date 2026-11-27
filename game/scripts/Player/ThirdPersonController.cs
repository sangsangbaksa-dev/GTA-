using System;
using Godot;
using Harbor.Core.Net;
using Harbor.Game.Bridge;

namespace Harbor.Game.Player;

public partial class ThirdPersonController : Node3D
{
    private const string MoveLeft = "walk_left";
    private const string MoveRight = "walk_right";
    private const string MoveForward = "walk_forward";
    private const string MoveBack = "walk_back";
    private const string Sprint = "walk_sprint";
    private const string Crouch = "walk_crouch";
    private const string Interact = "walk_interact";
    private const string Vehicle = "walk_vehicle";
    private const string Pickpocket = "walk_pickpocket";
    private const string Aim = "walk_aim";
    private const string Fire = "walk_fire";
    private const string Reload = "walk_reload";
    private const string Primary = "walk_primary";
    private const string Secondary = "walk_secondary";
    private const string Cycle = "walk_weapon_cycle";
    private const string Brake = "drive_brake";
    private const string VehicleThrottle = "drive_throttle";
    private const string VehicleReverse = "drive_reverse";
    private const string Pause = "walk_pause";
    private const string LookLeft = "walk_look_left";
    private const string LookRight = "walk_look_right";
    private const string LookUp = "walk_look_up";
    private const string LookDown = "walk_look_down";
    private const float InitialPitch = -0.30f;
    private const string FallbackModelPath = "res://assets/characters/chr_player_rook.glb";

    public event Action? PauseRequested;
    public bool IsPaused => _host.IsPaused;
    public bool IsCaptured => Input.MouseMode == Input.MouseModeEnum.Captured;
    public bool LastInputWasGamepad { get; private set; }
    public Camera3D Camera => _camera;
    public float Speed => _host.HasPlayer ? (float)_host.Client.Predicted.Velocity.Length : 0;
    public bool IsCrouched => _host.HasPlayer && _host.Client.Predicted.Crouched;
    public bool IsAiming { get; private set; }
    public bool AimHasTarget { get; private set; }
    public bool IsSprinting => _host.HasPlayer && _host.Client.Predicted.Sprinting;

    [Export] public float MouseSensitivity { get; set; } = 0.0025f;
    [Export] public float GamepadLookSpeed { get; set; } = 2.4f;
    [Export] public float CameraDistance { get; set; } = 4.25f;
    [Export(PropertyHint.File, "*.glb,*.tscn")]
    public string ModelPath { get; set; } = "res://assets/characters/chr_harbor_rook.glb";

    private CoreHost _host = null!;
    private Node3D _avatar = null!;
    private RookGait? _gait;
    private Camera3D _camera = null!;
    private float _yaw;
    private float _pitch = InitialPitch;
    private float _cameraHeight = 1.55f;
    private float _stride;
    private float _bodyAngle;
    private float _cameraArm = 1f;
    private int _seenGeneration;
    private byte _selectedSlot;
    private bool _wasDriving;
    private float _cameraDistance = 4.25f;
    private float _cameraShoulder = 0.65f;
    private float _manualOrbitLeft;

    public override void _Ready()
    {
        _host = GetNode<CoreHost>("../CoreHost");
        ProcessPriority = -1;
        RegisterAction(MoveLeft, KeyBinding(Key.A), KeyBinding(Key.Left), AxisBinding(JoyAxis.LeftX, -1));
        RegisterAction(MoveRight, KeyBinding(Key.D), KeyBinding(Key.Right), AxisBinding(JoyAxis.LeftX, 1));
        RegisterAction(MoveForward, KeyBinding(Key.W), KeyBinding(Key.Up), AxisBinding(JoyAxis.LeftY, -1));
        RegisterAction(MoveBack, KeyBinding(Key.S), KeyBinding(Key.Down), AxisBinding(JoyAxis.LeftY, 1));
        RegisterAction(LookLeft, AxisBinding(JoyAxis.RightX, -1));
        RegisterAction(LookRight, AxisBinding(JoyAxis.RightX, 1));
        RegisterAction(LookUp, AxisBinding(JoyAxis.RightY, -1));
        RegisterAction(LookDown, AxisBinding(JoyAxis.RightY, 1));
        RegisterAction(Sprint, KeyBinding(Key.Shift), ButtonBinding(JoyButton.LeftStick));
        RegisterAction(Crouch, KeyBinding(Key.C), ButtonBinding(JoyButton.B));
        RegisterAction(Interact, KeyBinding(Key.E), ButtonBinding(JoyButton.X));
        RegisterAction(Vehicle, KeyBinding(Key.F));
        RegisterAction(Pickpocket, KeyBinding(Key.G), ButtonBinding(JoyButton.LeftShoulder));
        RegisterAction(Aim, MouseBinding(MouseButton.Right), AxisBinding(JoyAxis.TriggerLeft, 1));
        RegisterAction(Fire, MouseBinding(MouseButton.Left), AxisBinding(JoyAxis.TriggerRight, 1));
        RegisterAction(Reload, KeyBinding(Key.R), ButtonBinding(JoyButton.RightShoulder));
        RegisterAction(Primary, KeyBinding(Key.Key1));
        RegisterAction(Secondary, KeyBinding(Key.Key2));
        RegisterAction(Cycle, ButtonBinding(JoyButton.Y));
        RegisterAction(Brake, KeyBinding(Key.Space), ButtonBinding(JoyButton.A));
        RegisterAction(VehicleThrottle, AxisBinding(JoyAxis.TriggerRight, 1));
        RegisterAction(VehicleReverse, AxisBinding(JoyAxis.TriggerLeft, 1));
        RegisterAction(Pause, KeyBinding(Key.Escape), ButtonBinding(JoyButton.Start));

        _avatar = new Node3D { Name = "Avatar", Visible = false };
        AddChild(_avatar);
        var path = !string.IsNullOrEmpty(ModelPath) && ResourceLoader.Exists(ModelPath) ? ModelPath : FallbackModelPath;
        if (ResourceLoader.Exists(path) && ResourceLoader.Load<PackedScene>(path) is { } model)
        {
            var rook = model.Instantiate<Node3D>();
            rook.Rotation = new Vector3(0, Mathf.Pi, 0);
            _avatar.AddChild(rook);
            _gait = RookGait.Create(rook);
        }
        else
        {
            AddPlaceholder();
        }

        _camera = new Camera3D { Name = "FollowCamera", Current = true, Fov = 68f, Near = 0.05f };
        AddChild(_camera);
        SetPaused(false);
    }

    public override void _ExitTree() => Input.MouseMode = Input.MouseModeEnum.Visible;

    public void SetPaused(bool paused)
    {
        _host.SetPaused(paused);
        Input.MouseMode = paused ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
    }

    public void ResetView()
    {
        _yaw = 0;
        _pitch = InitialPitch;
        _bodyAngle = 0;
        _stride = 0;
        _cameraHeight = 1.55f;
        _cameraArm = 1f;
        _cameraDistance = CameraDistance;
        _cameraShoulder = 0.65f;
        _selectedSlot = 0;
        _manualOrbitLeft = 0;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut && _host != null && !IsPaused)
        {
            PauseRequested?.Invoke();
            SetPaused(true);
        }
    }

    public override void _Input(InputEvent @event)
    {
        // Avoid mouse warps on capture and gamepad-axis drift switching the instruction glyphs.
        if (@event is InputEventJoypadButton { Pressed: true }
            || @event is InputEventJoypadMotion axis && Mathf.Abs(axis.AxisValue) > 0.35f)
            LastInputWasGamepad = true;
        else if (@event is InputEventKey { Pressed: true }
            or InputEventMouseButton { Pressed: true }
            || @event is InputEventMouseMotion mouse && IsCaptured && mouse.ScreenRelative.LengthSquared() > 1)
            LastInputWasGamepad = false;

        if (@event.IsActionPressed(Pause))
        {
            if (PauseRequested is { } requested) requested();
            else SetPaused(!IsPaused);
            GetViewport().SetInputAsHandled();
        }
        else if (!IsPaused && _host.HasPlayer)
        {
            if (@event is InputEventMouseMotion motion && IsCaptured)
            {
                UpdateLook(motion.ScreenRelative * MouseSensitivity * (IsAiming ? 0.7f : 1f));
                _manualOrbitLeft = 2f;
            }
            if (@event.IsActionPressed(Interact)) _host.QueueInputPulse(InputButtons.Interact);
            if (@event.IsActionPressed(Vehicle)) _host.QueueInputPulse(InputButtons.Vehicle);
            if (@event.IsActionPressed(Pickpocket)) _host.QueueInputPulse(InputButtons.Pickpocket);
            if (@event.IsActionPressed(Fire) && !_host.IsDriving) _host.QueueInputPulse(InputButtons.Fire);
            if (@event.IsActionPressed(Reload)) _host.QueueInputPulse(InputButtons.Reload);
            if (@event.IsActionPressed(Primary)) _selectedSlot = 0;
            if (@event.IsActionPressed(Secondary)) _selectedSlot = 1;
            if (@event.IsActionPressed(Cycle)) _selectedSlot = (byte)(1 - _selectedSlot);
        }
    }

    public override void _Process(double delta)
    {
        if (_seenGeneration != _host.RunGeneration)
        {
            ResetView();
            _seenGeneration = _host.RunGeneration;
        }

        var hasPlayer = _host.HasPlayer;
        var driving = _host.IsDriving;
        var enabled = hasPlayer && !IsPaused;
        _avatar.Visible = hasPlayer && !driving;
        if (!hasPlayer) return;

        var movement = Vector2.Zero;
        var buttons = InputButtons.None;
        IsAiming = enabled && !driving && Input.IsActionPressed(Aim);
        var frame = (float)delta;
        _manualOrbitLeft = Mathf.Max(0, _manualOrbitLeft - frame);
        if (enabled)
        {
            movement = Input.GetVector(MoveLeft, MoveRight, MoveBack, MoveForward);
            var look = Input.GetVector(LookLeft, LookRight, LookUp, LookDown);
            if (look.LengthSquared() > 0.001f)
            {
                UpdateLook(look * GamepadLookSpeed * frame * (IsAiming ? 0.68f : 1f));
                _manualOrbitLeft = 2f;
            }
            if (Input.IsActionPressed(Sprint) || driving && Input.IsActionPressed(Brake)) buttons |= InputButtons.Sprint;
            if (Input.IsActionPressed(Interact)) buttons |= InputButtons.Interact;
            if (Input.IsActionPressed(Vehicle)) buttons |= InputButtons.Vehicle;
            if (Input.IsActionPressed(Pickpocket)) buttons |= InputButtons.Pickpocket;
            if (driving)
            {
                movement = new Vector2(Input.GetAxis(MoveLeft, MoveRight), Input.GetAxis(MoveBack, MoveForward));
                var trigger = Input.GetAxis(VehicleReverse, VehicleThrottle);
                if (Mathf.Abs(trigger) > Mathf.Abs(movement.Y)) movement.Y = trigger;
                if (_manualOrbitLeft == 0 && Mathf.Abs((float)_host.Vehicle.Speed) > 1.2f)
                    _yaw = Mathf.LerpAngle(_yaw, (float)_host.Vehicle.Yaw, 1f - Mathf.Exp(-frame * 3.5f));
            }
            else
            {
                if (Input.IsActionPressed(Crouch)) buttons |= InputButtons.Crouch;
                if (Input.IsActionPressed(Fire)) buttons |= InputButtons.Fire;
                if (IsAiming) buttons |= InputButtons.Aim;
            }
        }

        var state = _host.Client.Predicted;
        var position = CoreHost.ToGodot(_host.RenderPosition);
        var velocity = CoreHost.ToGodot(state.Velocity);
        var blend = 1f - Mathf.Exp(-12f * frame);
        var speed = enabled && !driving ? velocity.Length() : 0;
        if (IsAiming || (buttons & InputButtons.Fire) != 0)
            _bodyAngle = Mathf.LerpAngle(_bodyAngle, _yaw, blend);
        else if (speed > 0.15f)
            _bodyAngle = Mathf.LerpAngle(_bodyAngle, Mathf.Atan2(-velocity.X, -velocity.Z), blend);

        if (driving != _wasDriving)
        {
            _cameraArm = 1f;
            _wasDriving = driving;
        }

        var localVelocity = new Basis(Vector3.Up, -_bodyAngle) * velocity;
        var retreating = localVelocity.Z > 0.3f;
        var cycle = state.Sprinting ? 2.45f : state.Crouched ? 4.45f : 3.35f;
        _stride = Mathf.PosMod(_stride + speed * frame * cycle * (retreating ? -1 : 1), Mathf.Tau);
        var strafe = speed > 0.2f ? Mathf.Clamp(localVelocity.X / speed, -1, 1) : 0;
        _gait?.Update(_stride, speed, state.Crouched, blend, frame, state.Sprinting, strafe: strafe);
        _avatar.GlobalPosition = position;
        _avatar.Rotation = new Vector3(0, _bodyAngle, 0);

        var targetHeight = driving ? 2.18f : state.Crouched ? 1.17f : 1.55f;
        _cameraHeight = Mathf.Lerp(_cameraHeight, targetHeight, blend);
        _cameraDistance = Mathf.Lerp(_cameraDistance, driving ? 6.7f : IsAiming ? 2.62f : CameraDistance, blend);
        _cameraShoulder = Mathf.Lerp(_cameraShoulder, driving ? 0 : IsAiming ? 0.76f : 0.65f, blend);
        _camera.Fov = Mathf.Lerp(_camera.Fov, driving ? 73 : IsAiming ? 52 : 68, blend);
        var pivot = position + Vector3.Up * _cameraHeight;
        var orbit = Basis.FromEuler(new Vector3(_pitch, _yaw, 0));
        var arm = orbit * new Vector3(_cameraShoulder, 0, Mathf.Max(0.5f, _cameraDistance));
        var length = arm.Length();
        var available = _host.Level.CameraObstruction(CoreHost.ToCore(pivot), CoreHost.ToCore(arm), length);
        var armTarget = available < length ? Mathf.Clamp(((float)available - 0.18f) / length, 0.035f, 1f) : 1f;
        _cameraArm = armTarget < _cameraArm ? armTarget : Mathf.Lerp(_cameraArm, armTarget, blend);
        _camera.GlobalPosition = pivot + arm * _cameraArm;
        _camera.GlobalRotation = new Vector3(_pitch, _yaw, 0);

        var aimYaw = _yaw;
        var aimPitch = _pitch;
        AimHasTarget = false;
        if (enabled && !driving)
        {
            var eye = _host.RenderPosition + new Harbor.Core.Math.Vec3d(0, (buttons & InputButtons.Crouch) != 0 ? 1.1 : 1.6, 0);
            var ray = CoreHost.ToCore(-orbit.Z);
            var target = _host.ResolveAim(CoreHost.ToCore(_camera.GlobalPosition), ray, out var hasTarget);
            AimHasTarget = hasTarget;
            var direction = target - eye;
            if (direction.Dot(ray) > 0.75)
            {
                aimYaw = (float)Math.Atan2(-direction.X, -direction.Z);
                aimPitch = (float)Math.Atan2(direction.Y, direction.WithY(0).Length);
                // The same input angles are also used by movement; preserve camera-relative movement.
                movement = movement.Rotated(_yaw - aimYaw);
            }
        }
        _host.SetMovement(movement, aimYaw, aimPitch, buttons, _selectedSlot);
    }

    private void UpdateLook(Vector2 amount)
    {
        _yaw = Mathf.Wrap(_yaw - amount.X, -Mathf.Pi, Mathf.Pi);
        _pitch = Mathf.Clamp(_pitch - amount.Y, -1.05f, 0.24f);
    }

    private static void RegisterAction(string action, params InputEvent[] bindings)
    {
        if (InputMap.HasAction(action)) return;
        InputMap.AddAction(action, 0.2f);
        foreach (var binding in bindings)
            InputMap.ActionAddEvent(action, binding);
    }

    private static InputEventMouseButton MouseBinding(MouseButton button) => new() { ButtonIndex = button };
    private static InputEventKey KeyBinding(Key key) => new() { PhysicalKeycode = key };
    private static InputEventJoypadButton ButtonBinding(JoyButton button)
        => new() { ButtonIndex = button, Device = -1 };
    private static InputEventJoypadMotion AxisBinding(JoyAxis axis, float direction)
        => new() { Axis = axis, AxisValue = direction, Device = -1 };

    private void AddPlaceholder()
    {
        var coat = new StandardMaterial3D { AlbedoColor = new Color("#235569"), Roughness = 0.9f };
        var head = new StandardMaterial3D { AlbedoColor = new Color("#a77554"), Roughness = 0.82f };
        _avatar.AddChild(new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = 0.28f, Height = 1.44f, Material = coat },
            Position = new Vector3(0, 0.72f, 0)
        });
        _avatar.AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.17f, Height = 0.34f, Material = head },
            Position = new Vector3(0, 1.56f, 0)
        });
    }

}
