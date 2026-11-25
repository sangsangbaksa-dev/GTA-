using Godot;

namespace Harbor.Game.Player;

/// <summary>Procedural locomotion and body weight for the imported Harbor character rigs.</summary>
internal sealed class RookGait
{
    private readonly Skeleton3D _rig;
    private readonly Leg _leftLeg;
    private readonly Leg _rightLeg;
    private readonly Arm _leftArm;
    private readonly Arm _rightArm;
    private readonly Bone _pelvis;
    private readonly Bone _spine;
    private readonly Bone _chest;
    private readonly Bone _neck;
    private readonly Vector3 _pelvisRest;
    private float _step;
    private float _crouch;
    private float _run;
    private float _strafe;
    private float _alert;
    private float _breath;

    private RookGait(Skeleton3D rig)
    {
        _rig = rig;
        _leftLeg = ReadLeg("L");
        _rightLeg = ReadLeg("R");
        _leftArm = ReadArm("L");
        _rightArm = ReadArm("R");
        _pelvis = ReadBone("hips");
        _spine = ReadBone("spine");
        _chest = ReadBone("chest");
        _neck = ReadBone("neck");
        if (_pelvis.Index >= 0) _pelvisRest = rig.GetBonePosePosition(_pelvis.Index);
    }

    public static RookGait? Create(Node3D model)
    {
        foreach (var node in model.FindChildren("*", "Skeleton3D", recursive: true, owned: false))
            if (node is Skeleton3D rig)
                return new RookGait(rig);

        return null;
    }

    public void Update(float phase, float speed, bool crouched, float blend, float dt = 0,
        bool sprinting = false, bool hurried = false, float strafe = 0)
    {
        _step = Mathf.Lerp(_step, Mathf.SmoothStep(0, 1, Mathf.Clamp(speed / 2.8f, 0, 1)), blend);
        _crouch = Mathf.Lerp(_crouch, crouched ? 1 : 0, blend);
        _run = Mathf.Lerp(_run, sprinting && !crouched ? 1 : 0, blend);
        _strafe = Mathf.Lerp(_strafe, strafe, blend);
        _alert = Mathf.Lerp(_alert, hurried ? 1 : 0, blend);
        _breath = Mathf.PosMod(_breath + dt * (2.35f + 2.6f * _run), Mathf.Tau);

        var swing = Mathf.Sin(phase);
        var breath = Mathf.Sin(_breath) * (1 - 0.70f * _step);
        var vertical = -0.37f * _crouch + (Mathf.Abs(Mathf.Cos(phase)) - 0.45f) * (0.038f + 0.033f * _run) * _step;
        if (_pelvis.Index >= 0)
        {
            var center = new Vector3(Mathf.Cos(phase) * 0.024f * _step, vertical, -0.14f * _crouch);
            _rig.SetBonePosePosition(_pelvis.Index, _pelvisRest + _pelvis.ToParent * center);
        }

        var turn = swing * (0.048f + 0.09f * _run) * _step;
        Pose(_pelvis, -0.07f * _crouch, -0.038f * swing * _step, turn);
        Pose(_spine, 0.34f * _crouch + 0.18f * _run, -0.075f * _strafe, -turn * 0.8f);
        Pose(_chest, breath * 0.016f + 0.06f * _alert, 0.035f * swing * _step, -turn);
        Pose(_neck, -0.08f * _alert - 0.06f * _run, 0.02f * breath, _alert * swing * 0.17f);

        PoseLeg(_leftLeg, swing, -1);
        PoseLeg(_rightLeg, -swing, 1);
        PoseArm(_leftArm, swing, -1);
        PoseArm(_rightArm, -swing, 1);
    }

    private void PoseLeg(Leg leg, float swing, float side)
    {
        var travel = _step * (1 - 0.28f * _crouch);
        var bend = 1.90f * _crouch + travel * Mathf.Pow(Mathf.Max(0, swing), 1.45f) * (0.44f + 0.47f * _run);
        var thigh = -travel * swing * (0.41f + 0.28f * _run) - 0.98f * _crouch;
        Pose(leg.Thigh, thigh, side * 0.02f - 0.11f * _strafe, 0.08f * side * _strafe);
        Pose(leg.Shin, bend);
        Pose(leg.Foot, -0.64f * (thigh + bend), 0, -0.025f * side);
    }

    private void PoseArm(Arm arm, float swing, float side)
    {
        var stride = (0.23f + 0.29f * _run) * _step * swing;
        var tuck = side * (0.69f - 0.045f * _run + 0.035f * _alert);
        Pose(arm.Upper, stride - _crouch * 0.15f - _alert * 0.08f, tuck);
        var elbow = 0.17f + 0.76f * _run + 0.13f * _alert + 0.11f * Mathf.Max(0, swing) * _step;
        Pose(arm.Fore, -elbow);
    }

    private Leg ReadLeg(string side) => new(ReadBone("thigh." + side), ReadBone("shin." + side), ReadBone("foot." + side));

    private Arm ReadArm(string side) => new(ReadBone("upper_arm." + side), ReadBone("forearm." + side));

    private Bone ReadBone(string name)
    {
        var index = _rig.FindBone(name);
        if (index < 0) return new Bone(-1, Quaternion.Identity, Basis.Identity);

        var parent = _rig.GetBoneParent(index);
        var parentBasis = parent < 0 ? Basis.Identity : _rig.GetBoneGlobalRest(parent).Basis.Orthonormalized();
        return new Bone(index, _rig.GetBonePoseRotation(index), parentBasis.Inverse());
    }

    private void Pose(Bone bone, float pitch, float roll = 0, float yaw = 0)
    {
        if (bone.Index < 0) return;

        var swing = new Quaternion(bone.ToParent * Vector3.Right, pitch);
        var lower = new Quaternion(bone.ToParent * Vector3.Back, roll);
        var turn = new Quaternion(bone.ToParent * Vector3.Up, yaw);
        _rig.SetBonePoseRotation(bone.Index, (turn * swing * lower * bone.RestRotation).Normalized());
    }

    private readonly record struct Leg(Bone Thigh, Bone Shin, Bone Foot);
    private readonly record struct Arm(Bone Upper, Bone Fore);
    private readonly record struct Bone(int Index, Quaternion RestRotation, Basis ToParent);
}
