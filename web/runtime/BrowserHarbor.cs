using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Harbor.Core.Combat;
using Harbor.Core.Data;
using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Ports;
using Harbor.Core.Server;
using Harbor.Core.Sim;
using Harbor.Core.Vehicles;
using Harbor.Core.World;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("browser")]

namespace Harbor.Browser;

public static class Program
{
    public static void Main() { }
}

public static partial class BrowserHarbor
{
    private const string LayoutPath = "data/world/harbor_front.json";
    private const string StreamingPath = "data/world/harbor_streaming.json";
    private static readonly JsonSerializerOptions WebJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static BrowserHost? _host;

    [JSExport]
    public static string Start()
    {
        _host = new BrowserHost();
        return _host.Step(0, 0, 0, 0, -0.30, 0, 0, false);
    }

    [JSExport]
    public static string Frame(
        double deltaSeconds,
        double moveRight,
        double moveForward,
        double yaw,
        double pitch,
        int heldButtons,
        int pulseButtons,
        int weaponSlot,
        bool dancing)
    {
        _host ??= new BrowserHost();
        return _host.Step(
            deltaSeconds,
            moveRight,
            moveForward,
            yaw,
            pitch,
            (InputButtons)(heldButtons | pulseButtons),
            (byte)Math.Clamp(weaponSlot, 0, 3),
            dancing);
    }

    [JSExport]
    public static void SetPaused(bool paused) => _host?.SetPaused(paused);

    [JSExport]
    public static void RestartLockdown() => _host?.RestartLockdown();

    [JSExport]
    public static void NewRun() => _host?.StartLocalSession();

    [JSExport]
    public static string ReadMapData()
    {
        _host ??= new BrowserHost();
        return JsonSerializer.Serialize(_host.ReadMap(), WebJson);
    }

    private sealed class BrowserHost
    {
        private readonly EmbeddedBrowserFiles _files = new();
        private readonly GameData _data;
        private readonly HarborLayout _layout;
        private readonly StaticWorld _level;
        private readonly HarborLockdownDef _encounter;
        private readonly HarborJobCatalog _jobs;
        private readonly StreetPopulationDef _ambient;
        private readonly ArcadeVehicleTuning _vehicleTuning;
        private readonly ChunkStreamingConfig _streaming;
        private readonly List<ShotFrame> _shots = new();
        private readonly List<DamageFrame> _damages = new();
        private readonly List<KillFrame> _kills = new();

        private HarborAuthority _authority = null!;
        private InMemoryNetwork _network = null!;
        private ClientSession _client = null!;
        private FixedTickLoop _clock = new();
        private HarborState _state = new();
        private HarborEnemyState[] _enemies = [];
        private Vec3d _previousPosition;
        private double _moveRight;
        private double _moveForward;
        private double _yaw;
        private double _pitch = -0.30;
        private double _cameraArm = 1.0;
        private double _cameraHeight = 1.55;
        private double _cameraDistance = 4.25;
        private double _cameraShoulder = 0.65;
        private InputButtons _buttons;
        private InputButtons _pulses;
        private byte _weaponSlot;
        private bool _paused = true;
        private bool _stepWhilePaused;
        private bool _wasDriving;
        private int _runGeneration;

        public BrowserHost()
        {
            _layout = HarborLayout.Load(_files, LayoutPath);
            _level = _layout.CreateStaticWorld();
            _data = GameData.LoadCombat(_files);
            _encounter = HarborLockdownDef.Load(_files, "data/missions/harbor_lockdown.json", _data.Weapons);
            _jobs = HarborJobCatalog.Load(_files, "data/missions/harbor_jobs.json");
            _ambient = StreetPopulationDef.Load(_files, "data/world/harbor_population.json");
            _vehicleTuning = ArcadeVehicleTuning.Load(_files, "data/vehicles/calloway_compact.json");
            _streaming = ChunkStreamingConfig.Load(_files, StreamingPath);
            StartLocalSession();
        }

        private bool HasPlayer => _client.IsJoined && _client.LocalServerState.Id == _client.LocalActorId;

        private Vec3d RenderPosition => _state.IsDriving
            ? _authority.Vehicle.Position
            : HasPlayer
                ? Vec3d.Lerp(_previousPosition, _client.Predicted.Position, _clock.Alpha)
                : _layout.SpawnPosition;

        public void StartLocalSession()
        {
            _runGeneration++;
            _clock = new FixedTickLoop();
            _state = new HarborState();
            _enemies = [];
            _pulses = InputButtons.None;
            _stepWhilePaused = false;
            _wasDriving = false;
            _network = new InMemoryNetwork { LatencySeconds = 0 };
            var serverEndpoint = _network.CreateServer();
            _authority = new HarborAuthority(
                _data,
                _encounter,
                _layout,
                _vehicleTuning,
                serverEndpoint,
                new BrowserLog(),
                maxPlayers: 1,
                jobs: _jobs,
                ambient: _ambient);
            _client = new ClientSession(_network.Connect(serverEndpoint), "Tomas", staticWorld: _level, interpolationDelayTicks: 0);
            _previousPosition = _layout.SpawnPosition;

            for (var i = 0; i < 8 && !HasPlayer; i++)
                Tick(1.0 / 30.0);
        }

        public void SetPaused(bool paused)
        {
            _paused = paused;
            if (paused) _pulses = InputButtons.None;
        }

        public void RestartLockdown()
        {
            if (!HasPlayer) return;
            _client.SendRequest(HarborProtocol.Restart, ReadOnlySpan<byte>.Empty);
            _stepWhilePaused = true;
        }

        public string Step(
            double deltaSeconds,
            double moveRight,
            double moveForward,
            double yaw,
            double pitch,
            InputButtons buttons,
            byte weaponSlot,
            bool dancing)
        {
            var dt = Math.Clamp(deltaSeconds, 0, 0.1);
            _shots.Clear();
            _damages.Clear();
            _kills.Clear();

            var aiming = !_paused && HasPlayer && !_state.IsDriving && (buttons & InputButtons.Aim) != 0;
            var firing = !_paused && HasPlayer && !_state.IsDriving && (buttons & InputButtons.Fire) != 0;

            _yaw = NormalizeAngle(yaw);
            _pitch = Math.Clamp(pitch, -1.05, 0.45);
            _moveRight = Math.Clamp(moveRight, -1, 1);
            _moveForward = Math.Clamp(moveForward, -1, 1);
            _buttons = buttons;
            _weaponSlot = weaponSlot;

            var camera = ComputeCamera(RenderPosition, dt, aiming);
            var aimPoint = ResolveAim(camera.Position, camera.Forward, out var aimHasTarget);
            if ((aiming || firing) && HasPlayer)
            {
                var eye = RenderPosition + new Vec3d(0, _client.Predicted.Crouched ? 1.1 : 1.6, 0);
                var toAim = aimPoint - eye;
                var planar = Math.Sqrt(toAim.X * toAim.X + toAim.Z * toAim.Z);
                if (planar > 0.001)
                {
                    _yaw = Math.Atan2(-toAim.X, -toAim.Z);
                    _pitch = Math.Atan2(toAim.Y, planar);
                }
            }

            if (!_paused || !HasPlayer || _stepWhilePaused)
                _clock.Advance(dt, Tick);

            camera = ComputeCamera(RenderPosition, 0, aiming);
            aimPoint = ResolveAim(camera.Position, camera.Forward, out aimHasTarget);

            var predicted = _client.Predicted;
            var playerFlags = _client.LocalServerState.Flags;
            var dead = HasPlayer && (playerFlags & ActorFlags.Dead) != 0;
            var speed = HasPlayer && !_state.IsDriving ? predicted.Velocity.WithY(0).Length : 0;
            var activeDance = dancing && !_paused && !_state.IsDriving && !predicted.Crouched && !aiming && !firing && speed <= 0.25;

            var vehicleVelocity = new Vec3d(
                -Math.Sin(_authority.Vehicle.Yaw) * _authority.Vehicle.Speed,
                0,
                -Math.Cos(_authority.Vehicle.Yaw) * _authority.Vehicle.Speed);

            var snapshot = new BrowserFrame(
                Ready: HasPlayer,
                Paused: _paused,
                RunGeneration: _runGeneration,
                IsDriving: _state.IsDriving,
                CanEnterVehicle: _state.CanEnterVehicle,
                WantedLevel: _state.WantedLevel,
                BestTime: _state.BestTime,
                WeaponSlot: _weaponSlot,
                Streaming: new StreamingFrame(
                    _streaming.ChunkSizeMeters,
                    _streaming.LoadRadiusChunks,
                    _streaming.UnloadPaddingChunks,
                    _streaming.LookaheadSeconds),
                Crime: new CrimeFrame(_state.Crime.Cash, _state.Crime.LastAction ?? ""),
                Player: new PlayerFrame(
                    Points(RenderPosition),
                    Points(_state.IsDriving ? vehicleVelocity : predicted.Velocity),
                    predicted.Yaw,
                    predicted.Crouched,
                    predicted.Sprinting,
                    dead,
                    activeDance),
                Camera: new CameraFrame(
                    Points(camera.Position),
                    Points(camera.Target),
                    camera.Yaw,
                    camera.Pitch,
                    camera.Fov,
                    aiming,
                    aimHasTarget,
                    Points(aimPoint)),
                Combat: new CombatFrame(
                    _state.PlayerCombat.Health,
                    _state.PlayerCombat.MaxHealth,
                    _state.PlayerCombat.Armor,
                    _state.PlayerCombat.MaxArmor,
                    _state.PlayerCombat.WeaponId ?? "",
                    _state.PlayerCombat.WeaponName ?? "",
                    _state.PlayerCombat.Magazine,
                    _state.PlayerCombat.Reserve,
                    _state.PlayerCombat.IsReloading,
                    _state.PlayerCombat.ActiveSlot),
                Vehicle: new VehicleFrame(
                    Points(_authority.Vehicle.Position),
                    _authority.Vehicle.Yaw,
                    _authority.Vehicle.Speed,
                    _authority.Vehicle.Steering,
                    _authority.Vehicle.Distance),
                Lockdown: new LockdownFrame(
                    _state.Lockdown.Phase.ToString(),
                    _state.Lockdown.RemainingEnemies,
                    _state.Lockdown.WaveNumber,
                    _state.Lockdown.WaveCount,
                    _state.Lockdown.Elapsed,
                    _state.Lockdown.ReinforcementSeconds,
                    Points(_state.Lockdown.StartPosition),
                    _state.Lockdown.StartRadius,
                    _state.Lockdown.CanStart,
                    Points(_state.Lockdown.ExtractionPosition),
                    _state.Lockdown.ExtractionRadius),
                Jobs: BuildJobsFrame(_state.Jobs),
                Ambient: BuildAmbientFrame(_state.Ambient),
                Enemies: _enemies.Select(enemy => new EnemyFrame(
                    enemy.Id,
                    enemy.Team,
                    Points(enemy.Position),
                    Points(enemy.Velocity),
                    enemy.Yaw,
                    (enemy.Flags & ActorFlags.Crouched) != 0,
                    (enemy.Flags & ActorFlags.Sprinting) != 0,
                    (enemy.Flags & ActorFlags.Aiming) != 0,
                    enemy.Health > 0 && (enemy.Flags & ActorFlags.Dead) == 0,
                    (enemy.Flags & ActorFlags.Downed) != 0,
                    enemy.Health,
                    enemy.MaxHealth)).ToArray(),
                Shots: _shots.ToArray(),
                Damages: _damages.ToArray(),
                Kills: _kills.ToArray());

            return JsonSerializer.Serialize(snapshot, WebJson);
        }

        public MapPayload ReadMap()
        {
            using var doc = JsonDocument.Parse(_files.ReadAllText(LayoutPath));
            var root = doc.RootElement;
            double[] min = [-215, -195];
            double[] max = [145, 95];
            if (root.TryGetProperty("bounds", out var bounds))
            {
                min = ReadPair(bounds.GetProperty("min"));
                max = ReadPair(bounds.GetProperty("max"));
            }

            var roads = ReadRects(root, "roads");
            var water = ReadRects(root, "water");
            var obstacles = _level.All
                .Where(box => (box.Box.Max.X - box.Box.Min.X) * (box.Box.Max.Z - box.Box.Min.Z) > 1.0)
                .Select(box => new[]
                {
                    Round(box.Box.Min.X),
                    Round(box.Box.Min.Z),
                    Round(box.Box.Max.X - box.Box.Min.X),
                    Round(box.Box.Max.Z - box.Box.Min.Z)
                })
                .ToArray();

            return new MapPayload(min, max, roads, water, obstacles);
        }

        private void Tick(double seconds)
        {
            if (_paused && HasPlayer && !_stepWhilePaused) return;
            _stepWhilePaused = false;
            var hadPlayer = HasPlayer;
            _previousPosition = _client.Predicted.Position;

            _network.Advance(seconds);
            _client.Poll();
            DrainEvents();

            if (HasPlayer)
            {
                var right = _paused ? 0 : _moveRight;
                var forward = _paused ? 0 : _moveForward;
                var buttons = _paused ? InputButtons.None : _buttons | _pulses;
                _client.Tick(right, forward, _yaw, _pitch, buttons, _weaponSlot);
                _pulses = InputButtons.None;
            }

            _network.Advance(0);
            _authority.Update(seconds);
            _network.Advance(0);
            _client.Poll();
            DrainEvents();

            if (!hadPlayer && HasPlayer)
                _previousPosition = _client.Predicted.Position;
        }

        private void DrainEvents()
        {
            while (_client.Events.TryDequeue(out var message))
            {
                switch (message.Type)
                {
                    case HarborProtocol.State when HarborProtocol.Decode<HarborState>(message.Payload) is
                        { Lockdown: not null, Jobs.Available: not null, Ambient.Pedestrians: not null,
                          Ambient.Traffic: not null, PlayerCombat: not null, Enemies: not null } state:
                        if (state.RunGeneration != _state.RunGeneration)
                        {
                            _runGeneration++;
                            _weaponSlot = 0;
                        }
                        _state = state;
                        _enemies = state.Enemies;
                        break;

                    case HarborProtocol.Shot when HarborProtocol.Decode<ShotFired>(message.Payload) is { } shot:
                        _shots.Add(new ShotFrame(
                            shot.ShooterId,
                            shot.ShooterId == _client.LocalActorId,
                            shot.WeaponId,
                            Points(shot.Origin),
                            Points(shot.End)));
                        break;

                    case HarborProtocol.Damage when HarborProtocol.Decode<ActorDamaged>(message.Payload) is { } damage:
                        _damages.Add(new DamageFrame(
                            damage.VictimId,
                            damage.AttackerId,
                            damage.HealthLost,
                            damage.ArmorLost,
                            damage.Source ?? "",
                            damage.VictimId == _client.LocalActorId,
                            damage.AttackerId == _client.LocalActorId));
                        break;

                    case HarborProtocol.Kill when HarborProtocol.Decode<ActorKilled>(message.Payload) is { } kill:
                        _kills.Add(new KillFrame(
                            kill.VictimId,
                            kill.KillerId,
                            kill.Source ?? "",
                            kill.Headshot,
                            kill.KillerId == _client.LocalActorId));
                        break;
                }
            }
        }

        private CameraSample ComputeCamera(Vec3d playerPos, double dt, bool aiming)
        {
            var driving = _state.IsDriving;
            var crouched = !driving && HasPlayer && _client.Predicted.Crouched;
            var blend = dt > 0 ? 1.0 - Math.Exp(-dt * 10.0) : 1.0;

            var targetHeight = driving ? 1.35 : crouched ? 1.05 : 1.55;
            var targetDist = driving ? 6.8 : aiming ? 2.45 : 4.25;
            var targetShoulder = driving ? 0.0 : aiming ? 0.78 : 0.65;
            var targetFov = driving
                ? 68.0 + Math.Clamp(Math.Abs(_authority.Vehicle.Speed) * 0.35, 0, 8.0)
                : aiming ? 50.0 : 68.0;

            if (driving != _wasDriving)
            {
                _wasDriving = driving;
                blend = Math.Max(blend, 0.35);
            }

            _cameraHeight = Lerp(_cameraHeight, targetHeight, blend);
            _cameraDistance = Lerp(_cameraDistance, targetDist, blend);
            _cameraShoulder = Lerp(_cameraShoulder, targetShoulder, blend);

            var pivot = playerPos + new Vec3d(0, _cameraHeight, 0);
            var forward = ForwardFromAngles(_yaw, _pitch);
            var right = new Vec3d(Math.Cos(_yaw), 0, -Math.Sin(_yaw));
            var desiredOffset = right * _cameraShoulder - forward * _cameraDistance + new Vec3d(0, 0.22, 0);
            var desiredLen = desiredOffset.Length;

            var safeFraction = 1.0;
            if (desiredLen > 0.05 && _level.Raycast(pivot, desiredOffset, desiredLen + 0.28, out var hit))
                safeFraction = Math.Clamp((hit.Distance - 0.28) / desiredLen, 0.14, 1.0);

            _cameraArm = safeFraction < _cameraArm
                ? safeFraction
                : Lerp(_cameraArm, safeFraction, dt > 0 ? Math.Min(1.0, dt * 8.0) : 1.0);

            var position = pivot + desiredOffset * _cameraArm;
            var lookTarget = pivot + forward * 16.0 + right * (_cameraShoulder * 0.35);
            return new CameraSample(position, lookTarget, forward, _yaw, _pitch, targetFov);
        }

        private Vec3d ResolveAim(Vec3d origin, Vec3d direction, out bool hasTarget)
        {
            var max = 90.0;
            if (_level.Raycast(origin, direction, max, out var block))
                max = block.Distance;

            hasTarget = false;
            foreach (var enemy in _enemies)
            {
                var alive = enemy.Health > 0 && (enemy.Flags & ActorFlags.Dead) == 0;
                if (!alive) continue;
                var crouched = (enemy.Flags & ActorFlags.Crouched) != 0;
                if (!Hitboxes.Raycast(origin, direction, max, new PoseSample(enemy.Position, crouched, true), out var distance, out _))
                    continue;
                max = distance;
                hasTarget = true;
            }
            return origin + direction * max;
        }

        private static JobsFrame BuildJobsFrame(HarborJobsState jobs) => new(
            jobs.Credits,
            jobs.Available.Select(o => new JobOfferFrame(
                o.Id,
                o.Title,
                o.Briefing,
                Points(o.StartPosition),
                o.Radius,
                o.Reward,
                o.Kind.ToString(),
                o.CanStart)).ToArray(),
            jobs.Active is { } a
                ? new JobActiveFrame(
                    a.Id,
                    a.Title,
                    a.Briefing,
                    a.Objective,
                    Points(a.Waypoint),
                    a.Radius,
                    a.Stage,
                    a.StageCount,
                    a.RemainingSeconds,
                    a.RemainingEnemies,
                    a.Reward,
                    a.Status.ToString(),
                    a.RunId,
                    a.Kind.ToString(),
                    a.StepType.ToString(),
                    a.CanInteract)
                : null,
            jobs.LastResult is { } r
                ? new JobResultFrame(
                    r.Id,
                    r.Title,
                    r.Status.ToString(),
                    r.Reward,
                    r.Elapsed,
                    r.RunId,
                    r.Reason)
                : null);

        private static AmbientFrame BuildAmbientFrame(StreetPopulationState ambient) => new(
            ambient.Pedestrians.Select(p => new PedestrianFrame(
                p.Id,
                p.Appearance,
                Points(p.Position),
                Round(p.Yaw),
                Round(p.Speed),
                p.Fleeing)).ToArray(),
            ambient.Traffic.Select(t => new TrafficFrame(
                t.Id,
                t.Appearance,
                Points(t.Position),
                Round(t.Yaw),
                Round(t.Speed))).ToArray());

        private static Vec3d ForwardFromAngles(double yaw, double pitch)
        {
            var cosPitch = Math.Cos(pitch);
            return new Vec3d(-Math.Sin(yaw) * cosPitch, Math.Sin(pitch), -Math.Cos(yaw) * cosPitch).Normalized();
        }

        private static double NormalizeAngle(double angle)
        {
            while (angle > Math.PI) angle -= Math.PI * 2.0;
            while (angle < -Math.PI) angle += Math.PI * 2.0;
            return angle;
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * Math.Clamp(t, 0, 1);
        private static double Round(double value) => Math.Round(value, 3);
        private static double[] Points(Vec3d v) => [Round(v.X), Round(v.Y), Round(v.Z)];

        private static double[] ReadPair(JsonElement el) => [el[0].GetDouble(), el[1].GetDouble()];

        private static double[][] ReadRects(JsonElement root, string prop)
        {
            if (!root.TryGetProperty(prop, out var list)) return [];
            return list.EnumerateArray().Select(item =>
            {
                var c = ReadPair(item.GetProperty("center"));
                var s = ReadPair(item.GetProperty("size"));
                return new[] { Round(c[0] - s[0] * 0.5), Round(c[1] - s[1] * 0.5), Round(s[0]), Round(s[1]) };
            }).ToArray();
        }
    }

    private readonly record struct CameraSample(Vec3d Position, Vec3d Target, Vec3d Forward, double Yaw, double Pitch, double Fov);

    private sealed record BrowserFrame(
        bool Ready,
        bool Paused,
        int RunGeneration,
        bool IsDriving,
        bool CanEnterVehicle,
        int WantedLevel,
        double? BestTime,
        int WeaponSlot,
        StreamingFrame Streaming,
        CrimeFrame Crime,
        PlayerFrame Player,
        CameraFrame Camera,
        CombatFrame Combat,
        VehicleFrame Vehicle,
        LockdownFrame Lockdown,
        JobsFrame Jobs,
        AmbientFrame Ambient,
        EnemyFrame[] Enemies,
        ShotFrame[] Shots,
        DamageFrame[] Damages,
        KillFrame[] Kills);

    private sealed record StreamingFrame(double ChunkSizeMeters, int LoadRadiusChunks, int UnloadPaddingChunks, double LookaheadSeconds);
    private sealed record CrimeFrame(int Cash, string LastAction);
    private sealed record PlayerFrame(double[] Position, double[] Velocity, double Yaw, bool Crouched, bool Sprinting, bool Dead, bool Dancing);
    private sealed record CameraFrame(double[] Position, double[] Target, double Yaw, double Pitch, double Fov, bool Aiming, bool AimHasTarget, double[] AimPoint);
    private sealed record CombatFrame(int Health, int MaxHealth, int Armor, int MaxArmor, string WeaponId, string WeaponName, int Magazine, int Reserve, bool IsReloading, int ActiveSlot);
    private sealed record VehicleFrame(double[] Position, double Yaw, double Speed, double Steering, double Distance);
    private sealed record LockdownFrame(string Phase, int RemainingEnemies, int WaveNumber, int WaveCount, double Elapsed, double ReinforcementSeconds, double[] StartPosition, double StartRadius, bool CanStart, double[] ExtractionPosition, double ExtractionRadius);
    private sealed record JobsFrame(long Credits, JobOfferFrame[] Available, JobActiveFrame? Active, JobResultFrame? LastResult);
    private sealed record JobOfferFrame(string Id, string Title, string Briefing, double[] StartPosition, double Radius, long Reward, string Kind, bool CanStart);
    private sealed record JobActiveFrame(string Id, string Title, string Briefing, string Objective, double[] Waypoint, double Radius, int Stage, int StageCount, double RemainingSeconds, int RemainingEnemies, long Reward, string Status, int RunId, string Kind, string StepType, bool CanInteract);
    private sealed record JobResultFrame(string Id, string Title, string Status, long Reward, double Elapsed, int RunId, string Reason);
    private sealed record AmbientFrame(PedestrianFrame[] Pedestrians, TrafficFrame[] Traffic);
    private sealed record PedestrianFrame(int Id, int Appearance, double[] Position, double Yaw, double Speed, bool Fleeing);
    private sealed record TrafficFrame(int Id, int Appearance, double[] Position, double Yaw, double Speed);
    private sealed record EnemyFrame(int Id, int Team, double[] Position, double[] Velocity, double Yaw, bool Crouched, bool Sprinting, bool Aiming, bool Alive, bool Downed, int Health, int MaxHealth);
    private sealed record ShotFrame(int ShooterId, bool Own, string WeaponId, double[] Origin, double[] End);
    private sealed record DamageFrame(int VictimId, int AttackerId, int HealthLost, int ArmorLost, string Source, bool VictimOwn, bool AttackerOwn);
    private sealed record KillFrame(int VictimId, int KillerId, string Source, bool Headshot, bool KillerOwn);
    private sealed record MapPayload(double[] Min, double[] Max, double[][] Roads, double[][] Water, double[][] Obstacles);

    private sealed class EmbeddedBrowserFiles : IFileSystem
    {
        private static readonly Assembly Assembly = typeof(BrowserHarbor).Assembly;
        private static readonly IReadOnlyDictionary<string, string> Paths = Assembly.GetManifestResourceNames()
            .ToDictionary(path => path.Replace('\\', '/'), StringComparer.Ordinal);

        public bool Exists(string path) => Paths.ContainsKey(path);

        public string ReadAllText(string path)
        {
            if (!Paths.TryGetValue(path, out var resource))
                throw new FileNotFoundException("Embedded browser data is missing", path);
            using var stream = Assembly.GetManifestResourceStream(resource)
                ?? throw new FileNotFoundException("Embedded browser data is missing", path);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public void WriteAllText(string path, string contents)
            => throw new NotSupportedException("Embedded browser data is read-only.");

        public IReadOnlyList<string> ListFiles(string directory, string extension)
        {
            var prefix = directory.TrimEnd('/') + "/";
            return Paths.Keys
                .Where(p => p.StartsWith(prefix, StringComparison.Ordinal)
                    && p.EndsWith(extension, StringComparison.Ordinal)
                    && p.IndexOf('/', prefix.Length) < 0)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private sealed class BrowserLog : ILog
    {
        public void Info(string msg) => Console.WriteLine(msg);
        public void Warn(string msg) => Console.WriteLine("[warn] " + msg);
        public void Error(string msg) => Console.Error.WriteLine(msg);
    }
}
