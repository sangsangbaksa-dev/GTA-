using Godot;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Sim;
using Harbor.Game.Bridge;
using Harbor.Game.Views;

namespace Harbor.Game.Player;

/// <summary>What the player wants this tick, already in core terms.</summary>
public readonly record struct PlayerIntent(double Right, double Forward, double Yaw, double Pitch, InputButtons Buttons, byte Slot);

/// <summary>Keyboard/mouse/gamepad → <see cref="PlayerIntent"/>, plus the over-the-shoulder camera.
/// The camera ray picks the aim point; the shot direction sent to the server is from the character's eye to that
/// point, so the crosshair is honest even though the camera sits behind the shoulder.</summary>
public partial class PlayerInput(GameSession session) : Node3D
{
    public Camera3D Camera { get; private set; } = null!;
    public bool UiOpen { get; set; }
    public float CamYaw { get; private set; }
    public float CamPitch { get; private set; } = -0.15f;
    public Vector3 AimPoint { get; private set; }

    private const float MouseSensitivity = 0.0026f;
    private byte _slot;
    private bool _shoulderLeft;
    private float _aimZoom;
    private bool _fireQueued, _reloadQueued, _interactHeld;
    private float _recoilPitch;
    /// <summary>Developer screenshots: orbit the camera around the character without turning it (radians).</summary>
    public float DevOrbit { get; set; }
    public float DevDistance { get; set; }
    /// <summary>Developer screenshots: buttons held regardless of the keyboard.</summary>
    public InputButtons DevHold { get; set; }

    public override void _Ready()
    {
        DefineActions();
        Camera = new Camera3D { Fov = 70, Far = 1500, Current = true };
        AddChild(Camera);
        session.Started += () => { if (session.Client != null) Input.MouseMode = Input.MouseModeEnum.Captured; };
        session.FxRaised += fx =>
        {
            if (fx is Harbor.Core.Client.ShotFx s && s.ShooterId == session.LocalActorId
                && session.Data.Weapons.TryGetValue(s.WeaponId, out var w)) _recoilPitch += (float)(w.Recoil.PitchDeg * Mathf.Pi / 180) * 0.6f;
            if (fx is Harbor.Core.Client.TeleportFx t) CamYaw = (float)session.Predicted.Yaw;
        };
    }

    private static void DefineActions()
    {
        void Key(string action, params Key[] keys)
        {
            if (!InputMap.HasAction(action)) InputMap.AddAction(action);
            foreach (var k in keys) InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = k });
        }
        void Mouse(string action, MouseButton b)
        {
            if (!InputMap.HasAction(action)) InputMap.AddAction(action);
            InputMap.ActionAddEvent(action, new InputEventMouseButton { ButtonIndex = b });
        }
        void Pad(string action, JoyButton b)
        {
            if (!InputMap.HasAction(action)) InputMap.AddAction(action);
            InputMap.ActionAddEvent(action, new InputEventJoypadButton { ButtonIndex = b });
        }
        Key("move_forward", Godot.Key.W); Key("move_back", Godot.Key.S); Key("move_left", Godot.Key.A); Key("move_right", Godot.Key.D);
        Key("sprint", Godot.Key.Shift); Key("crouch", Godot.Key.C, Godot.Key.Ctrl); Key("reload", Godot.Key.R); Key("interact", Godot.Key.E);
        Key("slot_1", Godot.Key.Key1); Key("slot_2", Godot.Key.Key2); Key("slot_3", Godot.Key.Key3); Key("slot_4", Godot.Key.Key4);
        Key("shoulder_swap", Godot.Key.Q); Key("phone", Godot.Key.Tab, Godot.Key.M); Key("drop_carry", Godot.Key.G);
        Key("ui_release_mouse", Godot.Key.Escape);
        Mouse("fire", MouseButton.Left); Mouse("aim", MouseButton.Right);
        Pad("sprint", JoyButton.LeftStick); Pad("crouch", JoyButton.B); Pad("reload", JoyButton.X); Pad("interact", JoyButton.A);
        Pad("phone", JoyButton.Back);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured && !UiOpen)
        {
            var sens = MouseSensitivity * (1 - 0.45f * _aimZoom);
            CamYaw -= mm.Relative.X * sens;
            CamPitch = Mathf.Clamp(CamPitch - mm.Relative.Y * sens, -1.35f, 1.2f);
        }
        if (e is InputEventMouseButton mb && mb.Pressed && Input.MouseMode != Input.MouseModeEnum.Captured && !UiOpen && session.Client != null)
            Input.MouseMode = Input.MouseModeEnum.Captured;
        if (e.IsActionPressed("ui_release_mouse")) Input.MouseMode = Input.MouseModeEnum.Visible;
        if (UiOpen) return;
        if (e.IsActionPressed("fire")) _fireQueued = true;
        if (e.IsActionPressed("reload")) _reloadQueued = true;
        if (e.IsActionPressed("shoulder_swap")) _shoulderLeft = !_shoulderLeft;
        for (var i = 0; i < 4; i++) if (e.IsActionPressed($"slot_{i + 1}")) _slot = (byte)i;
        if (e.IsActionPressed("drop_carry")) session.Request(Harbor.Core.Server.RequestType.CarryDrop);
    }

    /// <summary>Called once per client tick by the session.</summary>
    public PlayerIntent Sample()
    {
        var b = InputButtons.None;
        var blocked = UiOpen || Input.MouseMode != Input.MouseModeEnum.Captured;
        var move = blocked ? Vector2.Zero : Input.GetVector("move_left", "move_right", "move_back", "move_forward");
        // gamepad sticks
        var pad = new Vector2(Input.GetJoyAxis(0, JoyAxis.LeftX), -Input.GetJoyAxis(0, JoyAxis.LeftY));
        if (!UiOpen && pad.Length() > 0.2f) move = pad;
        if (!blocked)
        {
            if (Input.IsActionPressed("sprint")) b |= InputButtons.Sprint;
            if (Input.IsActionPressed("crouch")) b |= InputButtons.Crouch;
            if (Input.IsActionPressed("aim") || Input.GetJoyAxis(0, JoyAxis.TriggerLeft) > 0.4f) b |= InputButtons.Aim;
            if (Input.IsActionPressed("fire") || _fireQueued || Input.GetJoyAxis(0, JoyAxis.TriggerRight) > 0.4f) b |= InputButtons.Fire;
            if (Input.IsActionPressed("interact")) b |= InputButtons.Interact;
            if (_reloadQueued) b |= InputButtons.Reload;
        }
        _fireQueued = false;
        _reloadQueued = false;
        b |= DevHold;

        // aim: from the character's eye towards what the crosshair covers
        var me = session.Predicted;
        var eyeH = me.Crouched ? 1.1 : 1.6;
        var eye = me.Position + new Vec3d(0, eyeH, 0);
        var d = new Vec3d(AimPoint.X, AimPoint.Y, AimPoint.Z) - eye;
        double yaw = CamYaw, pitch = CamPitch;
        if (d.Length > 1.0)
        {
            var h = System.Math.Sqrt(d.X * d.X + d.Z * d.Z);
            pitch = System.Math.Atan2(d.Y, h);
            // when aiming or firing the body turns to the shot; otherwise the character faces where it walks (camera yaw)
            if ((b & (InputButtons.Aim | InputButtons.Fire)) != 0) yaw = System.Math.Atan2(-d.X, -d.Z);
        }
        return new PlayerIntent(move.X, move.Y, yaw, pitch, b, _slot);
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        var padLook = new Vector2(Input.GetJoyAxis(0, JoyAxis.RightX), Input.GetJoyAxis(0, JoyAxis.RightY));
        if (!UiOpen && padLook.Length() > 0.15f)
        {
            CamYaw -= padLook.X * dt * 2.6f;
            CamPitch = Mathf.Clamp(CamPitch - padLook.Y * dt * 1.8f, -1.35f, 1.2f);
        }
        // recoil kicks the view, then recovers
        CamPitch = Mathf.Clamp(CamPitch + _recoilPitch * 0.5f, -1.35f, 1.2f);
        _recoilPitch *= 0.5f;

        var aiming = (DevHold & InputButtons.Aim) != 0 || !UiOpen && (Input.IsActionPressed("aim") || Input.GetJoyAxis(0, JoyAxis.TriggerLeft) > 0.4f);
        _aimZoom = Mathf.MoveToward(_aimZoom, aiming ? 1 : 0, dt * 6);
        Camera.Fov = Mathf.Lerp(70, 48, _aimZoom);

        var a = session.PrevPredicted;
        var bS = session.Predicted;
        var p = Vec3d.Lerp(a.Position, bS.Position, session.Alpha);
        var dead = session.Client != null && (session.Client.LocalServerState.Flags & ActorFlags.Dead) != 0;
        var crouch = bS.Crouched ? 0.5f : 0;
        var pivot = ActorViews.V(p) + new Vector3(0, 1.55f - crouch, 0);
        var rot = new Basis(Vector3.Up, CamYaw + DevOrbit) * new Basis(Vector3.Right, CamPitch);
        var side = (_shoulderLeft ? -1 : 1) * Mathf.Lerp(0.55f, 0.65f, _aimZoom);
        var back = Mathf.Lerp(3.4f, 1.7f, _aimZoom) + (dead ? 3 : 0) + DevDistance;
        var offset = rot * new Vector3(side, 0.25f, back);
        // keep the camera out of walls (same collision the server uses)
        var from = new Vec3d(pivot.X, pivot.Y, pivot.Z);
        var dir = new Vec3d(offset.X, offset.Y, offset.Z);
        var len = dir.Length;
        if (session.ClientStatic.Raycast(from, dir, len + 0.3, out var hit)) len = System.Math.Max(0.4, hit.Distance - 0.3);
        var camPos = pivot + offset.Normalized() * (float)len;
        Camera.GlobalTransform = new Transform3D(rot, camPos);

        // crosshair ray → aim point (static geometry only; actors are close enough to the ray for hitscan to judge)
        var fwd = -rot.Z;
        var origin = new Vec3d(camPos.X, camPos.Y, camPos.Z);
        var ray = new Vec3d(fwd.X, fwd.Y, fwd.Z);
        var maxT = 300f;
        if (fwd.Y < -1e-3f) maxT = Mathf.Min(maxT, -camPos.Y / fwd.Y); // ground plane
        AimPoint = session.ClientStatic.Raycast(origin, ray, maxT, out var ah) ? ActorViews.V(ah.Point) : camPos + fwd * maxT;
        if (AimPoint.DistanceTo(pivot) < 2.0f) AimPoint = camPos + fwd * 60;
    }
}
