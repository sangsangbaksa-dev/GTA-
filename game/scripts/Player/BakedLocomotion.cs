using Godot;

namespace Harbor.Game.Player;

/// <summary>Samples the Harbor GLB clips in stride phase, then leaves the pose available to weapon IK.</summary>
internal sealed class BakedLocomotion
{
    private const float WalkStride = 1.27f;
    private const float JogStride = 1.85f;
    private const float RunStride = 2.64f;
    private const float CrouchStride = 0.94f;
    private const float WalkSpeed = WalkStride * 30 / 32;
    private const float JogSpeed = JogStride * 30 / 26;
    private const float RunSpeed = RunStride * 30 / 21;

    private readonly Skeleton3D _rig;
    private readonly Clip _idle;
    private readonly Clip _walk;
    private readonly Clip _jog;
    private readonly Clip _run;
    private readonly Clip _crouchIdle;
    private readonly Clip _crouchWalk;
    private readonly float _modelScale;
    private readonly int _hips;
    private readonly Quaternion[] _neutral;
    private readonly Vector3 _hipsOrigin;
    private readonly Foot _leftFoot;
    private readonly Foot _rightFoot;
    private float _previousPhase;
    private float _cycle;
    private float _idleTime;
    private float _speed;
    private float _moving;
    private float _crouching;
    private float _strafe;
    private float _groundLift;
    private bool _started;

    private BakedLocomotion(Skeleton3D rig, AnimationPlayer player)
    {
        _rig = rig;
        _idle = new Clip(rig, player.GetAnimation("Idle"));
        _walk = new Clip(rig, player.GetAnimation("Walk"));
        _jog = new Clip(rig, player.GetAnimation("Jog"));
        _run = new Clip(rig, player.GetAnimation("Run"));
        _crouchIdle = new Clip(rig, player.GetAnimation("CrouchIdle"));
        _crouchWalk = new Clip(rig, player.GetAnimation("CrouchWalk"));
        _neutral = new Quaternion[rig.GetBoneCount()];
        for (var bone = 0; bone < _neutral.Length; bone++)
            _neutral[bone] = rig.GetBoneRest(bone).Basis.GetRotationQuaternion().Normalized();
        _hips = rig.FindBone("hips");
        _hipsOrigin = _hips < 0 ? Vector3.Zero : rig.GetBoneRest(_hips).Origin;
        _modelScale = _hips < 0 ? 1f : rig.GetBoneGlobalRest(_hips).Origin.Y / 0.98f;
        _leftFoot = ReadFoot("L");
        _rightFoot = ReadFoot("R");
    }

    public static BakedLocomotion? Create(Node3D model, Skeleton3D rig)
    {
        var player = model.FindChild("AnimationPlayer", recursive: true, owned: false) as AnimationPlayer;
        if (player == null || !player.HasAnimation("Idle") || !player.HasAnimation("Walk") || !player.HasAnimation("Jog")
            || !player.HasAnimation("Run") || !player.HasAnimation("CrouchIdle") || !player.HasAnimation("CrouchWalk"))
            return null;

        return new BakedLocomotion(rig, player);
    }

    public void Update(float phase, float speed, bool crouched, float blend, float dt, bool sprinting, float strafe)
    {
        if (!_started)
        {
            _cycle = Mathf.PosMod(phase / Mathf.Tau, 1);
            _idleTime = _cycle * _idle.Length;
            _started = true;
        }

        // The caller's phase conveys forward/backward travel. The authored stride sets the cadence.
        var direction = Mathf.AngleDifference(_previousPhase, phase) < 0 ? -1f : 1f;
        _previousPhase = phase;
        var size = Mathf.Max(0.5f, _rig.GlobalBasis.X.Length() * _modelScale);
        _speed = Mathf.Lerp(_speed, speed / size, blend);
        _moving = Mathf.Lerp(_moving, Mathf.SmoothStep(0.07f, 0.82f, speed), blend);
        _crouching = Mathf.Lerp(_crouching, crouched ? 1f : 0f, blend);
        _strafe = Mathf.Lerp(_strafe, -strafe, blend);

        var selectionSpeed = sprinting && speed > 1f ? Mathf.Max(JogSpeed, _speed) : _speed;
        var jogWeight = Mathf.SmoothStep(WalkSpeed, JogSpeed, selectionSpeed);
        var runWeight = Mathf.SmoothStep(JogSpeed, RunSpeed, selectionSpeed);
        var low = runWeight > 0 ? _jog : _walk;
        var high = runWeight > 0 ? _run : _jog;
        var standingWeight = runWeight > 0 ? runWeight : jogWeight;
        var standingStride = runWeight > 0 ? Mathf.Lerp(JogStride, RunStride, runWeight)
            : Mathf.Lerp(WalkStride, JogStride, jogWeight);
        var distance = Mathf.Lerp(standingStride, CrouchStride, _crouching);
        _cycle = Mathf.PosMod(_cycle + _speed * direction * dt / distance, 1);
        _idleTime = Mathf.PosMod(_idleTime + dt, _idle.Length);

        var idleTime = _idleTime;
        var lowTime = _cycle * low.Length;
        var highTime = _cycle * high.Length;
        var crouchIdleTime = _idleTime / _idle.Length * _crouchIdle.Length;
        var crouchTime = _cycle * _crouchWalk.Length;
        for (var bone = 0; bone < _neutral.Length; bone++)
        {
            var neutral = _neutral[bone];
            var standing = _idle.Rotation(bone, idleTime, neutral);
            if (_moving > 0.001f)
            {
                var step = low.Rotation(bone, lowTime, neutral).Slerp(high.Rotation(bone, highTime, neutral), standingWeight).Normalized();
                standing = standing.Slerp(step, _moving).Normalized();
            }
            if (_crouching > 0.001f)
            {
                var crouch = _crouchIdle.Rotation(bone, crouchIdleTime, neutral)
                    .Slerp(_crouchWalk.Rotation(bone, crouchTime, neutral), _moving).Normalized();
                standing = standing.Slerp(crouch, _crouching);
            }
            if (bone == _hips && Mathf.Abs(_strafe) > 0.01f)
                standing = new Quaternion(Vector3.Up, _strafe * _moving * 0.20f) * standing;
            _rig.SetBonePoseRotation(bone, standing.Normalized());
        }

        if (_hips < 0) return;
        var standPosition = _idle.Position(_hips, idleTime, _hipsOrigin).Lerp(
            low.Position(_hips, lowTime, _hipsOrigin).Lerp(high.Position(_hips, highTime, _hipsOrigin), standingWeight), _moving);
        var crouchPosition = _crouchIdle.Position(_hips, crouchIdleTime, _hipsOrigin)
            .Lerp(_crouchWalk.Position(_hips, crouchTime, _hipsOrigin), _moving);
        var hipsPosition = standPosition.Lerp(crouchPosition, _crouching);
        _rig.SetBonePosePosition(_hips, hipsPosition);
        // Blending joint angles can lower a sole even when both source poses clear the floor.
        var floor = Mathf.Min(SoleHeight(_leftFoot), SoleHeight(_rightFoot));
        _groundLift = Mathf.Max(-floor, Mathf.MoveToward(_groundLift, 0, dt * 1.3f));
        if (_groundLift > 0.001f)
            _rig.SetBonePosePosition(_hips, hipsPosition + Vector3.Up * _groundLift);
    }

    private Foot ReadFoot(string side)
    {
        var bone = _rig.FindBone("foot." + side);
        if (bone < 0) return new Foot(-1, Vector3.Zero, Vector3.Zero);

        var rest = _rig.GetBoneGlobalRest(bone);
        var local = rest.AffineInverse();
        var heel = new Vector3(rest.Origin.X, 0, rest.Origin.Z - 0.044f * _modelScale);
        var toe = new Vector3(rest.Origin.X, 0, rest.Origin.Z + 0.178f * _modelScale);
        return new Foot(bone, local * heel, local * toe);
    }

    private float SoleHeight(Foot foot)
    {
        if (foot.Bone < 0) return 0;

        var pose = _rig.GetBoneGlobalPose(foot.Bone);
        return Mathf.Min((pose * foot.Heel).Y, (pose * foot.Toe).Y);
    }

    private readonly record struct Foot(int Bone, Vector3 Heel, Vector3 Toe);

    private sealed class Clip
    {
        private readonly Animation _animation;
        private readonly int[] _rotations;
        private readonly int[] _positions;
        public float Length => _animation.Length;

        public Clip(Skeleton3D rig, Animation animation)
        {
            _animation = animation;
            _rotations = new int[rig.GetBoneCount()];
            _positions = new int[rig.GetBoneCount()];
            System.Array.Fill(_rotations, -1);
            System.Array.Fill(_positions, -1);
            for (var track = 0; track < animation.GetTrackCount(); track++)
            {
                var path = animation.TrackGetPath(track);
                if (path.GetSubNameCount() == 0 || animation.TrackGetKeyCount(track) == 0) continue;
                var bone = rig.FindBone(path.GetSubName(0));
                if (bone < 0) continue;
                switch (animation.TrackGetType(track))
                {
                    case Animation.TrackType.Rotation3D:
                        _rotations[bone] = track;
                        break;
                    case Animation.TrackType.Position3D:
                        _positions[bone] = track;
                        break;
                }
            }
        }

        public Quaternion Rotation(int bone, float time, Quaternion fallback) =>
            _rotations[bone] >= 0 ? _animation.RotationTrackInterpolate(_rotations[bone], time).Normalized() : fallback;

        public Vector3 Position(int bone, float time, Vector3 fallback) =>
            _positions[bone] >= 0 ? _animation.PositionTrackInterpolate(_positions[bone], time) : fallback;
    }
}
