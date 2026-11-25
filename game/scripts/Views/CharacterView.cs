using Godot;
using Harbor.Core.Sim;

namespace Harbor.Game.Views;

/// <summary>One humanoid on screen: imported Blender character + weapon + name tag, posed procedurally from the
/// replicated movement state (velocity, yaw/pitch, crouch/aim/sprint/downed/dead flags).</summary>
public partial class CharacterView : Node3D
{
    public int ActorId { get; init; }
    public string ModelId { get; private set; } = "";
    public string WeaponId { get; private set; } = "";
    public Vector3 MuzzleWorld => _weapon != null ? _weapon.GlobalPosition + _weapon.GlobalBasis.Z * _weaponLength : GlobalPosition + Vector3.Up * 1.5f;

    private Node3D _pivot = null!;
    private Node3D? _model;
    private CharacterRig? _rig;
    private Node3D? _weapon;
    private float _weaponLength = 0.3f;
    private Label3D _tag = null!;
    private float _phase;
    private float _deadBlend;
    private float _downBlend;
    private float _crouchBlend;
    private float _aimBlend;

    public override void _Ready()
    {
        _pivot = new Node3D { Name = "Pivot" };
        AddChild(_pivot);
        _tag = new Label3D
        {
            Position = new Vector3(0, 2.15f, 0), PixelSize = 0.004f, FontSize = 40, OutlineSize = 10,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = false, Modulate = Colors.White,
        };
        AddChild(_tag);
    }

    public void SetModel(string modelId)
    {
        if (modelId == ModelId && _model != null) return;
        ModelId = modelId;
        _model?.QueueFree();
        _rig = null;
        var path = $"res://assets/characters/{modelId}.glb";
        if (!ResourceLoader.Exists(path)) path = "res://assets/characters/chr_npc_civilian.glb";
        _model = GD.Load<PackedScene>(path).Instantiate<Node3D>();
        _model.RotationDegrees = new Vector3(0, 180, 0); // glTF characters face +Z, Godot forward is -Z
        _pivot.AddChild(_model);
        var sk = FindSkeleton(_model);
        if (sk != null) _rig = new CharacterRig(sk, _model);
    }

    public void SetWeapon(string weaponId, string modelAsset)
    {
        if (weaponId == WeaponId) return;
        WeaponId = weaponId;
        _weapon?.QueueFree();
        _weapon = null;
        if (string.IsNullOrEmpty(modelAsset) || !ResourceLoader.Exists(modelAsset)) return;
        _weapon = GD.Load<PackedScene>(modelAsset).Instantiate<Node3D>();
        _weapon.TopLevel = true;
        AddChild(_weapon);
        _weaponLength = weaponId switch { "halberd_ar" => 0.68f, "breaker_12" => 0.6f, "wasp_smg" => 0.32f, _ => 0.11f };
    }

    public void SetTag(string text, Color color)
    {
        _tag.Text = text;
        _tag.Modulate = color;
        _tag.Visible = text.Length > 0;
    }

    private static Skeleton3D? FindSkeleton(Node n)
    {
        if (n is Skeleton3D s) return s;
        foreach (var c in n.GetChildren()) if (FindSkeleton(c) is { } f) return f;
        return null;
    }

    /// <summary>Drive the pose. Velocity in world space; yaw/pitch in core convention (yaw 0 = -Z).</summary>
    public void Animate(Vector3 position, Vector3 velocity, float yaw, float pitch, ActorFlags flags, float dt)
    {
        Position = position;
        Rotation = new Vector3(0, yaw, 0);
        var dead = (flags & ActorFlags.Dead) != 0;
        var downed = (flags & ActorFlags.Downed) != 0;
        _deadBlend = Mathf.MoveToward(_deadBlend, dead ? 1 : 0, dt * 2.5f);
        _downBlend = Mathf.MoveToward(_downBlend, downed ? 1 : 0, dt * 3f);
        _crouchBlend = Mathf.MoveToward(_crouchBlend, (flags & ActorFlags.Crouched) != 0 ? 1 : 0, dt * 6f);
        _aimBlend = Mathf.MoveToward(_aimBlend, (flags & ActorFlags.Aiming) != 0 ? 1 : 0, dt * 8f);
        _tag.Visible = !dead && _tag.Text.Length > 0;

        // dead: fall over backwards
        _pivot.Rotation = new Vector3(-Mathf.Pi / 2 * Smooth(_deadBlend), 0, 0);
        _pivot.Position = new Vector3(0, 0.15f * _deadBlend, 0);

        if (_rig == null) { PlaceWeapon(yaw, pitch); return; }

        // character-space velocity: +Z forward
        var local = Basis.Inverse() * velocity;
        var speed = new Vector2(local.X, local.Z).Length();
        var fwdSpeed = -local.Z; // node forward is -Z
        var sprint = (flags & ActorFlags.Sprinting) != 0;
        _phase += dt * (speed > 0.2f ? 2.1f + speed * 0.95f : 0) * (fwdSpeed >= -0.3f ? 1 : -1);
        var amp = Mathf.Clamp(speed / 6.2f, 0, 1) * (sprint ? 0.75f : 0.55f) * (1 - _crouchBlend * 0.5f);
        var sL = Mathf.Sin(_phase) * amp;
        var sR = -sL;

        var crouch = Mathf.Max(_crouchBlend, _downBlend);
        Leg("L", sL, Mathf.Max(0, Mathf.Cos(_phase)) * amp, crouch);
        Leg("R", sR, Mathf.Max(0, -Mathf.Cos(_phase)) * amp, crouch);

        var lean = (sprint ? 0.25f : 0.06f * amp) + crouch * 0.35f;
        _rig.Point("spine", new Vector3(0, 1, lean));
        var p = pitch * 0.5f * (1 - _downBlend);
        _rig.Point("chest", new Vector3(0, Mathf.Cos(p), -Mathf.Sin(p) + lean * 0.5f));
        _rig.Point("neck", new Vector3(0, Mathf.Cos(p), -Mathf.Sin(p)));

        var aimDir = new Vector3(0, Mathf.Sin(pitch), Mathf.Cos(pitch));
        var armed = _weapon != null && !downed;
        var pistol = WeaponId == "kestrel_p10";
        if (armed && pistol && _aimBlend < 0.05f)
        {
            // pistol at the side, free hand relaxed
            var swing = sL * 0.6f;
            _rig.Point("upper_arm.L", new Vector3(0.2f, -1, -swing));
            _rig.Point("forearm.L", new Vector3(0.08f, -1, 0.25f - swing * 0.5f));
            _rig.Point("upper_arm.R", new Vector3(-0.22f, -1, 0.1f + swing * 0.4f));
            _rig.Point("forearm.R", new Vector3(-0.1f, -0.9f, 0.45f));
        }
        else if (armed)
        {
            var a = _aimBlend;
            // right arm: low ready → shoulder the weapon
            _rig.Point("upper_arm.R", Lerp(new Vector3(-0.25f, -0.85f, 0.45f), aimDir + new Vector3(-0.35f, -0.35f, 0), a));
            _rig.Point("forearm.R", Lerp(new Vector3(0.25f, -0.2f, 1f), aimDir + new Vector3(0.12f, 0.02f, 0), a));
            _rig.Point("upper_arm.L", Lerp(new Vector3(0.05f, -0.65f, 0.75f), aimDir + new Vector3(-0.45f, -0.4f, 0.1f), a));
            _rig.Point("forearm.L", Lerp(new Vector3(-0.45f, 0.1f, 1f), aimDir + new Vector3(-0.35f, 0.05f, 0), a));
        }
        else
        {
            var swing = sL * 0.8f;
            _rig.Point("upper_arm.L", new Vector3(0.2f, -1, -swing));
            _rig.Point("forearm.L", new Vector3(0.08f, -1, 0.25f - swing * 0.5f));
            _rig.Point("upper_arm.R", new Vector3(-0.2f, -1, swing));
            _rig.Point("forearm.R", new Vector3(-0.08f, -1, 0.25f + swing * 0.5f));
            if (downed)
            {
                _rig.Point("upper_arm.R", new Vector3(-0.3f, -0.6f, 0.6f));
                _rig.Point("forearm.R", new Vector3(0.2f, -0.6f, 0.7f));
            }
        }
        var bob = speed > 0.2f ? Mathf.Abs(Mathf.Cos(_phase)) * 0.035f * amp * 2 : 0;
        _rig.Apply(new Vector3(0, -0.38f * _crouchBlend - 0.5f * _downBlend + bob - 0.03f * amp, 0.05f * crouch));
        PlaceWeapon(yaw, pitch);
    }

    private void Leg(string side, float swing, float lift, float crouch)
    {
        // angles from straight down, positive = forward
        var th = swing + crouch * 1.05f;
        var knee = 0.12f + lift * 1.1f + crouch * 1.9f;
        _rig!.Point($"thigh.{side}", new Vector3(0, -Mathf.Cos(th), Mathf.Sin(th)));
        var sh = th - knee;
        _rig.Point($"shin.{side}", new Vector3(0, -Mathf.Cos(sh), Mathf.Sin(sh)));
        _rig.Point($"foot.{side}", new Vector3(0, -0.35f, 1));
    }

    private void PlaceWeapon(float yaw, float pitch)
    {
        if (_weapon == null) return;
        var aimWorld = new Vector3(-Mathf.Sin(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch), -Mathf.Cos(yaw) * Mathf.Cos(pitch));
        Vector3 grip;
        if (_rig != null && _deadBlend < 0.01f)
        {
            var hand = _rig.BoneGlobal("hand.R");
            grip = _rig.Skeleton.GlobalTransform * (hand.Origin + hand.Basis.Y.Normalized() * 0.06f);
        }
        else grip = GlobalPosition + Vector3.Up * 1.2f;
        var lowered = WeaponId == "kestrel_p10" ? 0.9f : 0.45f;
        var fwd = _aimBlend > 0.5f || _rig == null ? aimWorld : (aimWorld * 0.5f + Vector3.Down * lowered).Normalized();
        _weapon.GlobalTransform = new Transform3D(Basis.LookingAt(-fwd, Vector3.Up), grip);
        _weapon.Visible = _deadBlend < 0.5f;
    }

    private static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a.Lerp(b, t);
    private static float Smooth(float t) => t * t * (3 - 2 * t);
}
