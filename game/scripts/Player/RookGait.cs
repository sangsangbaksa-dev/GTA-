using Godot;

namespace Harbor.Game.Player;

/// <summary>Presentation-only stride for the imported Rook rig, which has no animation clips.</summary>
internal sealed class RookGait
{
    private readonly Skeleton3D _rig;
    private readonly Leg _leftLeg;
    private readonly Leg _rightLeg;
    private readonly Bone _leftArm;
    private readonly Bone _rightArm;
    private float _amount;
    private float _crouch;

    private RookGait(Skeleton3D rig)
    {
        _rig = rig;
        _leftLeg = new Leg(ReadBone("thigh.L"), ReadBone("shin.L"));
        _rightLeg = new Leg(ReadBone("thigh.R"), ReadBone("shin.R"));
        _leftArm = ReadBone("upper_arm.L");
        _rightArm = ReadBone("upper_arm.R");
    }

    public static RookGait? Create(Node3D model)
    {
        foreach (var node in model.FindChildren("*", "Skeleton3D", recursive: true, owned: false))
            if (node is Skeleton3D rig)
                return new RookGait(rig);

        return null;
    }

    public void Update(float phase, float speed, bool crouched, float blend)
    {
        _amount = Mathf.Lerp(_amount, Mathf.Clamp(speed / 4f, 0, 1), blend);
        _crouch = Mathf.Lerp(_crouch, crouched ? 1 : 0, blend);
        var swing = Mathf.Sin(phase);
        PoseLeg(_leftLeg, swing);
        PoseLeg(_rightLeg, -swing);
        Pose(_leftArm, 0.25f * _amount * swing, -0.28f);
        Pose(_rightArm, -0.25f * _amount * swing, 0.28f);
    }

    private void PoseLeg(Leg leg, float swing)
    {
        Pose(leg.Thigh, -0.47f * _amount * swing - 0.26f * _crouch);
        Pose(leg.Shin, 0.47f * _amount * Mathf.Max(0, swing) + 0.41f * _crouch);
    }

    private Bone ReadBone(string name)
    {
        var index = _rig.FindBone(name);
        if (index < 0) return new Bone(-1, Quaternion.Identity, Basis.Identity);

        var parent = _rig.GetBoneParent(index);
        var parentBasis = parent < 0 ? Basis.Identity : _rig.GetBoneGlobalRest(parent).Basis.Orthonormalized();
        return new Bone(index, _rig.GetBonePoseRotation(index), parentBasis.Inverse());
    }

    private void Pose(Bone bone, float pitch, float roll = 0)
    {
        if (bone.Index < 0) return;

        var swing = new Quaternion(bone.ToParent * Vector3.Right, pitch);
        var lower = new Quaternion(bone.ToParent * Vector3.Back, roll);
        _rig.SetBonePoseRotation(bone.Index, (swing * lower * bone.RestRotation).Normalized());
    }

    private readonly record struct Leg(Bone Thigh, Bone Shin);
    private readonly record struct Bone(int Index, Quaternion RestRotation, Basis ToParent);
}
