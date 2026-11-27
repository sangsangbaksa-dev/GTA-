using System;
using System.Collections.Generic;
using Godot;
using Harbor.Core.Combat;
using Harbor.Core.Sim;
using Harbor.Game.Bridge;
using Harbor.Game.Player;

namespace Harbor.Game.Views;

/// <summary>Renders accepted combat state and plays combat cues without feeding back into simulation.</summary>
public partial class CombatPresentation : Node3D
{
    private const string AssetRoot = "res://assets/";
    private static readonly (string Preferred, string Fallback)[] CharacterPaths =
    {
        ("chr_rook_courier", "chr_player_rook"),
        ("chr_harbor_crew", "chr_enemy_gunman"),
        ("chr_harbor_officer", "chr_enemy_enforcer")
    };
    private static readonly Dictionary<string, string> Weapons = new()
    {
        ["kestrel_p10"] = "wpn_pistol_kestrel",
        ["wasp_smg"] = "wpn_smg_wasp",
        ["halberd_ar"] = "wpn_rifle_halberd",
        ["breaker_12"] = "wpn_shotgun_breaker"
    };

    private readonly PackedScene[] _characters = new PackedScene[CharacterPaths.Length];
    private bool _patrolHasShield;
    private readonly Dictionary<string, PackedScene> _weapons = new();
    private readonly Dictionary<string, AudioStream> _sounds = new();
    private readonly Dictionary<int, CharacterView> _views = new();
    private readonly HashSet<int> _seen = new();
    private readonly List<int> _expired = new();
    private readonly List<LightEffect> _effects = new();
    private readonly Random _variation = new(613);
    private CoreHost _host = null!;
    private ThirdPersonController _player = null!;
    private WeaponPose? _playerPose;
    private AudioStreamPlayer? _ambience;
    private AudioStreamPlayer3D? _siren;
    private string _weaponId = "";
    private float _playerFlinch;
    private float _weaponKick;
    private float _stepDistance;
    private Vector3? _lastPlayerPosition;
    private bool _reloading;
    private int _generation;

    public override void _Ready()
    {
        _host = GetNode<CoreHost>("../CoreHost");
        _player = GetNode<ThirdPersonController>("../Player");
        _playerPose = WeaponPose.Create(_player.GetNode<Node3D>("Avatar"));
        ProcessPriority = 20;

        for (var i = 0; i < _characters.Length; i++)
        {
            var (preferred, fallback) = CharacterPaths[i];
            var path = AssetRoot + "characters/" + preferred + ".glb";
            if (!ResourceLoader.Exists(path))
            {
                path = AssetRoot + "characters/" + fallback + ".glb";
                if (i == 2) _patrolHasShield = true;
            }
            _characters[i] = GD.Load<PackedScene>(path);
        }
        foreach (var (id, path) in Weapons)
            _weapons[id] = GD.Load<PackedScene>(AssetRoot + "weapons/" + path + ".glb");

        if (DisplayServer.GetName() != "headless") InitializeAudio();

        _host.ShotFired += OnShot;
        _host.ActorDamaged += OnDamage;
        _host.ActorKilled += OnDeath;
    }

    private void InitializeAudio()
    {
        foreach (var name in new[]
                 {
                     "dock_ambience", "district_alert", "kestrel", "wasp", "halberd", "breaker", "impact", "hit",
                     "hurt", "reload", "ready", "swap", "step_1", "step_2", "step_3"
                 })
        {
            var stream = GD.Load<AudioStreamOggVorbis>(AssetRoot + "audio/" + name + ".ogg");
            stream.Loop = name is "dock_ambience" or "district_alert";
            _sounds[name] = stream;
        }

        _ambience = new AudioStreamPlayer { Stream = _sounds["dock_ambience"], VolumeDb = -3.0f };
        AddChild(_ambience);
        _ambience.Play();

        _siren = new AudioStreamPlayer3D
        {
            Stream = _sounds["district_alert"], VolumeDb = -60, UnitSize = 8, MaxDistance = 145
        };
        AddChild(_siren);
    }

    public override void _ExitTree()
    {
        _ambience?.Stop();
        _siren?.Stop();
        if (!GodotObject.IsInstanceValid(_host)) return;

        _host.ShotFired -= OnShot;
        _host.ActorDamaged -= OnDamage;
        _host.ActorKilled -= OnDeath;
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        if (_generation != _host.RunGeneration)
        {
            _generation = _host.RunGeneration;
            ResetViews();
        }

        _seen.Clear();
        var blend = 1f - Mathf.Exp(-16f * dt);
        Vector3? patrolPosition = null;
        foreach (var actor in _host.Enemies)
        {
            UpdateCharacter(actor, dt, blend);
            if (actor.Team == 3 && actor.IsAlive)
                patrolPosition ??= CoreHost.ToGodot(actor.Position);
        }

        _expired.Clear();
        foreach (var id in _views.Keys)
            if (!_seen.Contains(id)) _expired.Add(id);
        foreach (var id in _expired)
        {
            _views[id].Root.QueueFree();
            _views.Remove(id);
        }

        UpdatePlayer(dt, blend);
        UpdateSiren(patrolPosition, dt);
        UpdateEffects(dt);
    }

    private void ResetViews()
    {
        foreach (var view in _views.Values) view.Root.QueueFree();
        _views.Clear();
        foreach (var effect in _effects) effect.Node.QueueFree();
        _effects.Clear();
        _lastPlayerPosition = null;
        _stepDistance = 0;
        _weaponKick = 0;
        _playerFlinch = 0;
        _weaponId = "";
        _reloading = false;
        _playerPose?.Equip("", null);
    }

    private void UpdateCharacter(Actor actor, float dt, float blend)
    {
        _seen.Add(actor.Id);
        var position = CoreHost.ToGodot(actor.Position);
        if (!_views.TryGetValue(actor.Id, out var view))
        {
            var remote = actor.Kind == ActorKind.Player;
            var modelIndex = remote ? 0 : actor.Team == 3 ? 2 : 1;
            var root = new Node3D { Name = $"Actor_{actor.Id}", Position = position, Rotation = new Vector3(0, (float)actor.Move.Yaw, 0) };
            var model = _characters[modelIndex].Instantiate<Node3D>();
            model.Rotation = new Vector3(0, Mathf.Pi, 0);
            root.AddChild(model);
            AddChild(root);

            var gait = RookGait.Create(model);
            var pose = WeaponPose.Create(model, shielded: actor.Team == 3 && _patrolHasShield);
            var weaponId = remote ? "wasp_smg" : "kestrel_p10";
            pose?.Equip(weaponId, _weapons[weaponId]);
            view = new CharacterView(root, model, gait, pose, actor.Id);
            if (actor.Team == 3) view.Beacon = AddBeacon(model);
            _views.Add(actor.Id, view);
        }

        view.Root.GlobalPosition = view.Root.GlobalPosition.DistanceSquaredTo(position) > 36f
            ? position
            : view.Root.GlobalPosition.Lerp(position, blend);
        var heading = Mathf.LerpAngle(view.Root.Rotation.Y, (float)actor.Move.Yaw, blend);
        view.Root.Rotation = new Vector3(0, heading, 0);
        var collapsed = !actor.IsAlive || (actor.Flags & ActorFlags.Downed) != 0;

        if (collapsed)
        {
            view.FallAge += dt;
            var fall = Mathf.Clamp(view.FallAge / 0.46f, 0, 1);
            var arc = Mathf.Sin(fall * Mathf.Pi / 2f);
            view.Model.Position = new Vector3(0, 0.17f * arc, 0);
            view.Model.Rotation = new Vector3(-Mathf.Pi / 2f * arc, Mathf.Pi, 0.16f * fall * view.FallSide);
            view.Pose?.Update(0.1f, 0, 0, 0, true, blend);
            view.Gait?.Update(view.Phase, 0, false, blend);
            if (view.Beacon != null) view.Beacon.Visible = false;
            return;
        }

        view.FallAge = 0;
        view.Flinch = Mathf.MoveToward(view.Flinch, 0, dt * 4.8f);
        view.Kick = Mathf.MoveToward(view.Kick, 0, dt * 6.5f);
        var speed = (float)actor.Move.Velocity.WithY(0).Length;
        var sprinting = (actor.Flags & ActorFlags.Sprinting) != 0;
        var local = new Basis(Vector3.Up, -heading) * CoreHost.ToGodot(actor.Move.Velocity);
        var strafe = speed > 0.2f ? Mathf.Clamp(local.X / speed, -1, 1) : 0;
        view.Phase = Mathf.PosMod(view.Phase + Mathf.Min(speed, 9f) * dt * (sprinting ? 2.45f : 3.35f)
            * (local.Z > 0.3f ? -1 : 1), Mathf.Tau);
        view.Model.Position = Vector3.Zero;
        view.Model.Rotation = new Vector3(view.Flinch * 0.15f, Mathf.Pi, 0.11f * view.Flinch * view.FallSide);
        view.Gait?.Update(view.Phase, speed, actor.Move.Crouched, blend, dt, sprinting, strafe: strafe);
        var activelyAiming = (actor.Flags & ActorFlags.Aiming) != 0 || view.Kick > 0;
        var raised = activelyAiming ? 1f : actor.Kind == ActorKind.Player || sprinting ? 0.22f : 0.79f;
        view.Pose?.Update(raised, (float)actor.Move.Pitch, view.Kick, 0, false, blend);
        if (view.Beacon != null)
            view.Beacon.Visible = Mathf.PosMod((float)Time.GetTicksMsec() * 0.001f + actor.Id * 0.11f, 1.1f) < 0.15f;
    }

    private void UpdatePlayer(float dt, float blend)
    {
        if (!_host.HasPlayer) return;
        var status = _host.PlayerCombat;
        var equipped = status.WeaponId ?? "";
        if (_weaponId != equipped)
        {
            if (_weaponId.Length > 0 && !_host.IsPaused) PlayLocal("swap", -6);
            _weaponId = equipped;
            _playerPose?.Equip(equipped, _weapons.GetValueOrDefault(equipped));
        }

        if (status.IsReloading != _reloading)
        {
            _reloading = status.IsReloading;
            if (!_host.IsPaused) PlayLocal(_reloading ? "reload" : "ready", -5);
        }

        _weaponKick = Mathf.MoveToward(_weaponKick, 0, dt * 5.6f);
        _playerFlinch = Mathf.MoveToward(_playerFlinch, 0, dt * 6.5f);
        if (!_host.IsDriving)
        {
            var camera = _player.Camera;
            var raised = _player.IsAiming ? 1f : _weaponKick > 0 ? 0.86f : _player.IsSprinting ? 0.10f : 0.31f;
            var pitch = _player.IsAiming || _weaponKick > 0 ? camera.GlobalRotation.X : 0;
            _playerPose?.Update(raised, pitch, _weaponKick, (_reloading ? 1f : 0) + _playerFlinch * 0.22f,
                status.Health <= 0, blend);
        }

        var position = CoreHost.ToGodot(_host.RenderPosition);
        var distance = _lastPlayerPosition is { } previous ? (position - previous).Length() : 0;
        _lastPlayerPosition = position;
        if (_host.IsPaused || _host.IsDriving || distance > 2f || _player.Speed < 0.15f)
        {
            _stepDistance = 0;
            return;
        }

        _stepDistance += distance;
        var interval = _player.IsCrouched ? 0.57f : 0.96f;
        if (_stepDistance < interval) return;
        _stepDistance %= interval;
        PlayLocal("step_" + _variation.Next(1, 4), _player.IsCrouched ? -14 : -9);
    }

    private void UpdateSiren(Vector3? patrolPosition, float dt)
    {
        if (_siren == null) return;

        if (patrolPosition is { } position && _host.WantedLevel > 0 && !_host.IsPaused)
        {
            if (!_siren.Playing) _siren.Play();
            _siren.GlobalPosition = position + new Vector3(-5, 1.5f, 8);
            _siren.VolumeDb = Mathf.MoveToward(_siren.VolumeDb, -17 + Mathf.Min(3, _host.WantedLevel), dt * 46);
            return;
        }

        if (!_siren.Playing) return;
        _siren.VolumeDb = Mathf.MoveToward(_siren.VolumeDb, -60, dt * 60);
        if (_siren.VolumeDb <= -59) _siren.Stop();
    }

    private void OnShot(ShotFired shot)
    {
        var own = _host.Client.LocalActorId == shot.ShooterId;
        var origin = CoreHost.ToGodot(shot.Origin);
        var end = CoreHost.ToGodot(shot.End);
        var direction = (end - origin).Normalized();
        if (direction.LengthSquared() < 0.5f) direction = Vector3.Forward;
        WeaponPose? pose = null;
        if (own)
        {
            _weaponKick = Mathf.Clamp(_weaponKick + 0.78f, 0, 1);
            pose = _playerPose;
        }
        else if (_views.TryGetValue(shot.ShooterId, out var view))
        {
            view.Kick = 0.9f;
            pose = view.Pose;
            pose?.Equip(shot.WeaponId, _weapons.GetValueOrDefault(shot.WeaponId));
        }

        var muzzle = pose?.MuzzlePosition ?? origin + direction * 0.47f + Vector3.Down * 0.16f;
        // Very near obstructions can place the gameplay hit before the animated muzzle.
        if ((end - muzzle).Dot(direction) < 0.05f)
            muzzle = origin + (end - origin) * 0.18f;

        var sound = shot.WeaponId switch
        {
            "wasp_smg" => "wasp",
            "halberd_ar" => "halberd",
            "breaker_12" => "breaker",
            _ => "kestrel"
        };
        if (own) PlayLocal(sound, -2.5f);
        else PlayAt(sound, muzzle, -4);

        var flashColor = own ? new Color("#fff1bb") : new Color("#ffb775");
        AddFlash(muzzle, direction, flashColor, own ? 0.91f : 0.65f);
        AddTracer(muzzle, end, own ? new Color("#fff1a3") : new Color("#ff7955"));
        var range = origin.DistanceTo(end);
        if (range > 0.25f && end.DistanceSquaredTo(_player.Camera.GlobalPosition) < 900)
        {
            // Only report world strikes here. Accepted actor hits have their own cues.
            if (_host.Level.Raycast(shot.Origin, shot.End - shot.Origin, range + 0.08, out var hit)
                && Mathf.Abs((float)hit.Distance - range) < 0.16f)
            {
                AddImpact(end, CoreHost.ToGodot(hit.Normal));
                PlayAt("impact", end, -13);
            }
        }
    }

    private void OnDamage(ActorDamaged damage)
    {
        if (_views.TryGetValue(damage.VictimId, out var view)) view.Flinch = 1;
        var own = _host.Client.LocalActorId;
        if (damage.VictimId == own)
        {
            _playerFlinch = 1;
            PlayLocal("hurt", -6);
        }
        else if (damage.AttackerId == own)
        {
            PlayLocal("hit", damage.ArmorLost > 0 ? -11 : -7);
        }
    }

    private void OnDeath(ActorKilled death)
    {
        if (!_views.TryGetValue(death.VictimId, out var view)) return;

        view.Flinch = 1;
        if (death.KillerId == _host.Client.LocalActorId)
            PlayLocal("ready", -13);
    }

    private void AddFlash(Vector3 muzzle, Vector3 direction, Color color, float strength)
    {
        var anchor = new Node3D { Position = muzzle, Basis = Basis.LookingAt(direction, Vector3.Up) };
        var material = EffectMaterial(color);
        AddChild(anchor);
        for (var index = 0; index < 2; index++)
        {
            var flash = new MeshInstance3D
            {
                Mesh = new PrismMesh { Size = new Vector3(0.18f, 0.39f, 0.012f) * strength },
                MaterialOverride = material,
                Position = new Vector3(0, 0, -0.12f * strength),
                Basis = new Basis(Vector3.Back, index * Mathf.Pi / 2f) * new Basis(Vector3.Right, -Mathf.Pi / 2f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            anchor.AddChild(flash);
        }
        var lamp = new OmniLight3D { LightColor = color, LightEnergy = 1.48f * strength, OmniRange = 3.4f };
        anchor.AddChild(lamp);
        _effects.Add(new LightEffect(anchor, material, 0.06f, lamp));
    }

    private void AddTracer(Vector3 from, Vector3 to, Color color)
    {
        var distance = from.DistanceTo(to);
        if (distance < 0.25f) return;

        var direction = (to - from) / distance;
        var streakFrom = from + direction * Mathf.Min(0.22f, distance * 0.25f);
        var streakLength = Mathf.Min(distance * 0.84f, 16f);
        var beam = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.008f, 0.008f, streakLength) },
            Position = streakFrom + direction * streakLength * 0.5f,
            Basis = Basis.LookingAt(direction, Vector3.Up),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        var material = EffectMaterial(color);
        beam.MaterialOverride = material;
        AddChild(beam);
        _effects.Add(new LightEffect(beam, material, 0.087f));
    }

    private void AddImpact(Vector3 position, Vector3 normal)
    {
        for (var i = 0; i < 5; i++)
        {
            var spread = new Vector3(RandomSigned(), RandomSigned(), RandomSigned());
            var velocity = (normal + spread * 0.72f).Normalized() * (0.7f + (float)_variation.NextDouble() * 3f);
            var spark = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.014f, 0.014f, 0.095f) },
                Position = position + normal * 0.045f,
                Basis = Basis.LookingAt(velocity, Vector3.Up),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            var material = EffectMaterial(i % 2 == 0 ? new Color("#fff4b1") : new Color("#ffa052"));
            spark.MaterialOverride = material;
            AddChild(spark);
            _effects.Add(new LightEffect(spark, material, 0.11f + (float)_variation.NextDouble() * 0.12f)
                { Velocity = velocity });
        }
    }

    private void UpdateEffects(float dt)
    {
        for (var i = _effects.Count - 1; i >= 0; i--)
        {
            var effect = _effects[i];
            effect.TimeLeft -= effect.HasDrawn ? dt : 0;
            effect.HasDrawn = true;
            if (effect.TimeLeft <= 0)
            {
                effect.Node.QueueFree();
                _effects.RemoveAt(i);
                continue;
            }

            var ratio = effect.TimeLeft / effect.Duration;
            var color = effect.Material.AlbedoColor;
            color.A = Mathf.SmoothStep(0, 0.68f, ratio);
            effect.Material.AlbedoColor = color;
            if (effect.Lamp != null) effect.Lamp.LightEnergy = effect.InitialEnergy * ratio;
            if (effect.Velocity.LengthSquared() <= 0) continue;

            effect.Node.Position += effect.Velocity * dt;
            effect.Velocity += Vector3.Down * (5.5f * dt);
        }
    }

    private void PlayLocal(string cue, float db)
    {
        if (!_sounds.TryGetValue(cue, out var sound)) return;
        var player = new AudioStreamPlayer { Stream = sound, VolumeDb = db, PitchScale = RandomPitch() };
        AddChild(player);
        player.Finished += player.QueueFree;
        player.Play();
    }

    private void PlayAt(string cue, Vector3 position, float db)
    {
        if (!_sounds.TryGetValue(cue, out var sound)) return;
        var player = new AudioStreamPlayer3D
        {
            Stream = sound, VolumeDb = db, Position = position, UnitSize = 6.5f, MaxDistance = 105,
            PitchScale = RandomPitch()
        };
        AddChild(player);
        player.Finished += player.QueueFree;
        player.Play();
    }

    private float RandomSigned() => (float)_variation.NextDouble() * 2 - 1;
    private float RandomPitch() => 1f + RandomSigned() * 0.055f;

    private static MeshInstance3D AddBeacon(Node3D model)
    {
        var beacon = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.09f, 0.04f, 0.059f) },
            Position = new Vector3(-0.20f, 1.55f, 0.055f),
            MaterialOverride = EffectMaterial(new Color("#48baff")),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        model.AddChild(beacon);
        return beacon;
    }

    private static StandardMaterial3D EffectMaterial(Color color) => new()
    {
        AlbedoColor = color,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        EmissionEnabled = true,
        Emission = color,
        EmissionEnergyMultiplier = 1.22f
    };

    private sealed class CharacterView(Node3D root, Node3D model, RookGait? gait, WeaponPose? pose, int id)
    {
        public readonly Node3D Root = root;
        public readonly Node3D Model = model;
        public readonly RookGait? Gait = gait;
        public readonly WeaponPose? Pose = pose;
        public readonly float FallSide = id % 2 == 0 ? 1 : -1;
        public float Phase = id * 1.73f;
        public float FallAge;
        public float Flinch;
        public float Kick;
        public MeshInstance3D? Beacon;
    }

    private sealed class LightEffect(Node3D node, StandardMaterial3D material, float duration, OmniLight3D? lamp = null)
    {
        public readonly Node3D Node = node;
        public readonly StandardMaterial3D Material = material;
        public readonly float Duration = duration;
        public readonly OmniLight3D? Lamp = lamp;
        public readonly float InitialEnergy = lamp?.LightEnergy ?? 0;
        public float TimeLeft = duration;
        public bool HasDrawn;
        public Vector3 Velocity;
    }

    /// <summary>Places the authored props at the hands and blends a two-bone arm pose with the walk cycle.</summary>
    private sealed class WeaponPose
    {
        private readonly Skeleton3D _rig;
        private readonly Arm _right;
        private readonly Arm _left;
        private readonly bool _shielded;
        private readonly int _hips;
        private readonly float _restHipsHeight;
        private readonly float _torsoRatio;
        private Node3D? _weapon;
        private Node3D? _muzzle;
        private Node3D? _foregrip;
        private string _id = "";
        private float _raised;
        private float _pitch;
        private float _reload;
        public Vector3? MuzzlePosition => _muzzle is { } muzzle && muzzle.IsVisibleInTree()
            ? muzzle.GlobalPosition
            : null;

        private WeaponPose(Skeleton3D rig, bool shielded)
        {
            _rig = rig;
            _shielded = shielded;
            _right = ReadArm("R");
            _left = ReadArm("L");
            _hips = _rig.FindBone("hips");
            _restHipsHeight = _hips >= 0 ? _rig.GetBoneGlobalRest(_hips).Origin.Y : 1f;
            _torsoRatio = _right.Upper >= 0 ? _rig.GetBoneGlobalRest(_right.Upper).Origin.Y / 1.48f : 1;
        }

        public static WeaponPose? Create(Node3D actor, bool shielded = false)
        {
            var children = actor.FindChildren("*", "Skeleton3D", recursive: true, owned: false);
            return children.Count > 0 && children[0] is Skeleton3D rig ? new WeaponPose(rig, shielded) : null;
        }

        public void Equip(string id, PackedScene? scene)
        {
            if (_id == id) return;
            _id = id;
            _weapon?.QueueFree();
            _weapon = null;
            _muzzle = null;
            _foregrip = null;
            if (scene == null) return;

            _weapon = scene.Instantiate<Node3D>();
            _weapon.Name = "HeldWeapon";
            _rig.AddChild(_weapon);
            _muzzle = _weapon.FindChild("muzzle", recursive: true, owned: false) as Node3D;
            _foregrip = _weapon.FindChild("grip_off", recursive: true, owned: false) as Node3D;
        }

        public void Update(float raised, float pitch, float kick, float reload, bool hidden, float blend)
        {
            if (_weapon == null || _right.Upper < 0 || _left.Upper < 0) return;

            _weapon.Visible = !hidden;
            if (hidden) return;

            _raised = Mathf.Lerp(_raised, raised, blend);
            _pitch = Mathf.Lerp(_pitch, pitch, blend);
            _reload = Mathf.Lerp(_reload, reload, blend);
            var rightSign = Mathf.Sign(_rig.GetBoneGlobalRest(_right.Upper).Origin.X);
            var hipShift = _hips >= 0 ? _rig.GetBoneGlobalPose(_hips).Origin.Y - _restHipsHeight : 0;
            var pistol = _id == "kestrel_p10";
            var rightHand = new Vector3(rightSign * (0.29f - _raised * (pistol ? 0.16f : 0.09f)),
                (1.04f + _raised * 0.38f - _reload * 0.17f) * _torsoRatio + hipShift,
                0.20f + _raised * (pistol ? 0.24f : 0.16f) - kick * 0.052f);
            var raise = -_pitch * _raised + (1 - _raised) * (pistol ? 0.85f : 0.59f) - kick * 0.15f + _reload * 0.39f;
            var twist = (_reload * -0.31f + (1 - _raised) * 0.10f) * rightSign;
            var weaponBasis = Basis.FromEuler(new Vector3(raise, -0.19f * (1 - _raised) * rightSign, twist));
            _weapon.Transform = new Transform3D(weaponBasis, rightHand);
            Solve(_right, rightHand - weaponBasis.Z * 0.064f, new Vector3(rightSign * 0.8f, -1, -0.3f), weaponBasis.Z);
            if (_shielded)
            {
                HoldShield(-rightSign);
                return;
            }

            var support = _foregrip is { } grip
                ? _weapon.Transform * grip.Position
                : rightHand + new Vector3(-rightSign * 0.05f, -0.035f, 0.027f);
            support = support.Lerp(new Vector3(-rightSign * 0.26f, 1.04f * _torsoRatio + hipShift, 0.16f), _reload * 0.9f);
            Solve(_left, support - weaponBasis.Z * 0.063f, new Vector3(-rightSign * 0.95f, -1, -0.20f), weaponBasis.Z);
        }

        private void HoldShield(float side)
        {
            // Keep the shield on its authored forearm bone and square the face toward the encounter.
            var parent = _rig.GetBoneGlobalPose(_rig.GetBoneParent(_left.Upper));
            var rest = parent * _rig.GetBoneRest(_left.Upper);
            var elbowDirection = new Vector3(side * 0.12f, -0.39f, 0.76f);
            var upperRotation = SwingTo(rest.Basis, _left.UpperAxis, elbowDirection);
            var shieldTurn = new Basis(Vector3.Up, -side * 0.39f);
            var guard = shieldTurn * _rig.GetBoneGlobalRest(_left.Fore).Basis.Orthonormalized();
            _rig.SetBonePoseRotation(_left.Upper, (parent.Basis.Orthonormalized().Inverse() * upperRotation).GetRotationQuaternion());
            _rig.SetBonePoseRotation(_left.Fore, (upperRotation.Inverse() * guard).GetRotationQuaternion());
        }

        private Arm ReadArm(string side)
        {
            var upper = _rig.FindBone("upper_arm." + side);
            var fore = _rig.FindBone("forearm." + side);
            var hand = _rig.FindBone("hand." + side);
            if (upper < 0 || fore < 0 || hand < 0) return new Arm(-1, -1, -1, 0, 0, Vector3.Up, Vector3.Up);

            var foreRest = _rig.GetBoneRest(fore).Origin;
            var handRest = _rig.GetBoneRest(hand).Origin;
            return new Arm(upper, fore, hand, foreRest.Length(), handRest.Length(), foreRest.Normalized(), handRest.Normalized());
        }

        private void Solve(Arm arm, Vector3 target, Vector3 swing, Vector3 palmDirection)
        {
            var parent = _rig.GetBoneGlobalPose(_rig.GetBoneParent(arm.Upper));
            var upperRest = parent * _rig.GetBoneRest(arm.Upper);
            var origin = upperRest.Origin;
            var reach = target - origin;
            var direction = reach.Normalized();
            var length = Mathf.Clamp(reach.Length(), Mathf.Abs(arm.UpperLength - arm.ForeLength) + 0.002f,
                arm.UpperLength + arm.ForeLength - 0.001f);
            var elbowAlong = (arm.UpperLength * arm.UpperLength - arm.ForeLength * arm.ForeLength + length * length) / (2 * length);
            var bend = Mathf.Sqrt(Mathf.Max(0, arm.UpperLength * arm.UpperLength - elbowAlong * elbowAlong));
            var elbow = origin + direction * elbowAlong + (swing - direction * swing.Dot(direction)).Normalized() * bend;
            var goal = origin + direction * length;
            var upperRotation = SwingTo(upperRest.Basis, arm.UpperAxis, elbow - origin);
            _rig.SetBonePoseRotation(arm.Upper, (parent.Basis.Orthonormalized().Inverse() * upperRotation).GetRotationQuaternion());
            var foreRest = upperRotation * _rig.GetBoneRest(arm.Fore).Basis;
            var foreRotation = SwingTo(foreRest, arm.ForeAxis, goal - elbow);
            _rig.SetBonePoseRotation(arm.Fore, (upperRotation.Inverse() * foreRotation).GetRotationQuaternion());
            var palmRest = foreRotation * _rig.GetBoneRest(arm.Hand).Basis;
            var palmRotation = SwingTo(palmRest, Vector3.Up, palmDirection);
            _rig.SetBonePoseRotation(arm.Hand, (foreRotation.Inverse() * palmRotation).GetRotationQuaternion());
        }

        private static Basis SwingTo(Basis rest, Vector3 axis, Vector3 target)
        {
            var clean = rest.Orthonormalized();
            return new Basis(new Quaternion((clean * axis).Normalized(), target.Normalized())) * clean;
        }

        private readonly record struct Arm(int Upper, int Fore, int Hand, float UpperLength, float ForeLength, Vector3 UpperAxis, Vector3 ForeAxis);
    }
}
