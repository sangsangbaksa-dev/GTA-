using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Harbor.Core.Combat;
using Harbor.Core.Data;
using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Server;
using Harbor.Core.Sim;
using Harbor.Core.Vehicles;
using Harbor.Game.Adapters;

namespace Harbor.Game.Bridge;

public enum HarborConnectionStatus { Connecting, Ready, Disconnected, Failed }

/// <summary>Drives the same server authority locally or connects the renderer and predictive client to a dedicated host.</summary>
public partial class CoreHost : Node
{
    private const string LayoutPath = "data/world/harbor_front.json";

    public event Action<ShotFired>? ShotFired;
    public event Action<ActorDamaged>? ActorDamaged;
    public event Action<ActorKilled>? ActorKilled;

    public ClientSession Client { get; private set; } = null!;
    public StaticWorld Level { get; private set; } = null!;
    public HarborLockdownState Lockdown => _state.Lockdown;
    public HarborPlayerCombat PlayerCombat => _state.PlayerCombat;
    public double? BestTime => _state.BestTime;
    public int WantedLevel => _state.WantedLevel;
    public bool IsDriving => _state.IsDriving;
    public bool CanEnterVehicle => _state.CanEnterVehicle;
    public int DriverId => _state.DriverId;
    public int RunGeneration { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsRemote { get; private set; }
    public HarborConnectionStatus ConnectionStatus { get; private set; }
    public string ConnectionMessage { get; private set; } = "";
    public ArcadeVehicle Vehicle { get; private set; } = null!;
    public IEnumerable<Actor> Enemies => _enemies;
    public IEnumerable<Actor> RemotePlayers => _otherPlayers;
    public bool HasPlayer => Client is { IsJoined: true } && Client.LocalServerState.Id == Client.LocalActorId;
    public Vec3d RenderPosition => IsDriving ? Vehicle.Position
        : HasPlayer ? Vec3d.Lerp(_previousPosition, Client.Predicted.Position, _clock.Alpha) : _layout.SpawnPosition;

    private HarborAuthority? _authority;
    private InMemoryNetwork? _localNetwork;
    private TcpTransport? _remoteTransport;
    private CancellationTokenSource? _connecting;
    private Task<TcpTransport>? _pendingTransport;
    private double _handshakeSeconds;
    private GameData _data = null!;
    private HarborLayout _layout = null!;
    private HarborLockdownDef _encounter = null!;
    private ArcadeVehicleTuning _vehicleTuning = null!;
    private FixedTickLoop _clock = new();
    private HarborState _state = new();
    private HarborVehicleState _vehicleTarget;
    private bool _vehicleInitialized;
    private Actor[] _enemies = [];
    private Actor[] _otherPlayers = [];
    private Vec3d _previousPosition;
    private Vector2 _movement;
    private float _yaw;
    private float _pitch;
    private InputButtons _buttons;
    private InputButtons _pulses;
    private byte _weaponSlot;
    private bool _stepWhilePaused;

    public override void _Ready()
    {
        var files = new EmbeddedGameFiles();
        _layout = HarborLayout.Load(files, LayoutPath);
        Level = _layout.CreateStaticWorld();
        _data = GameData.LoadCombat(files);
        _encounter = HarborLockdownDef.Load(files, "data/missions/harbor_lockdown.json", _data.Weapons);
        _vehicleTuning = ArcadeVehicleTuning.Load(files, "data/vehicles/calloway_compact.json");

        var args = OS.GetCmdlineUserArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var target = args[i].StartsWith("--connect=", StringComparison.Ordinal) ? args[i][10..]
                : args[i] == "--connect" && i + 1 < args.Length ? args[i + 1] : "";
            if (target.Length == 0) continue;
            var split = target.LastIndexOf(':');
            if (split > 0 && int.TryParse(target[(split + 1)..], out var port))
            {
                ConnectToServer(target[..split].Trim('[', ']'), port);
                return;
            }
        }
        StartLocalSession();
    }

    public override void _ExitTree() => CloseSession();

    public void StartLocalSession()
    {
        CloseSession();
        ResetClientState();
        IsRemote = false;
        ConnectionStatus = HarborConnectionStatus.Connecting;
        ConnectionMessage = "Starting local authority…";
        _localNetwork = new InMemoryNetwork { LatencySeconds = 0 };
        var serverEndpoint = _localNetwork.CreateServer();
        _authority = new HarborAuthority(_data, _encounter, _layout, _vehicleTuning, serverEndpoint, new GodotLog(), maxPlayers: 1);
        Client = new ClientSession(_localNetwork.Connect(serverEndpoint), "Rook", staticWorld: Level, interpolationDelayTicks: 0);
    }

    public void ConnectToServer(string host, int port)
    {
        CloseSession();
        ResetClientState();
        IsRemote = true;
        ConnectionStatus = HarborConnectionStatus.Connecting;
        ConnectionMessage = $"Connecting to {host}:{port}…";
        _connecting = new CancellationTokenSource();
        _pendingTransport = TcpTransport.ConnectAsync(host, port, _connecting.Token);
    }

    private void FinishConnection()
    {
        if (_pendingTransport is not { IsCompleted: true } pending) return;
        _pendingTransport = null;
        _connecting?.Dispose();
        _connecting = null;
        try
        {
            _remoteTransport = pending.GetAwaiter().GetResult();
            Client = new ClientSession(_remoteTransport, "Rook", staticWorld: Level);
            ConnectionMessage = "Waiting for the harbor server…";
        }
        catch (Exception error)
        {
            ConnectionStatus = HarborConnectionStatus.Failed;
            ConnectionMessage = error.Message;
        }
    }

    public void RestartLockdown()
    {
        if (!HasPlayer) return;
        Client.SendRequest(HarborProtocol.Restart, ReadOnlySpan<byte>.Empty);
        _stepWhilePaused = true;
    }

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        if (paused) _pulses = InputButtons.None;
    }

    public void QueueInputPulse(InputButtons buttons) => _pulses |= buttons;

    public void SetMovement(Vector2 movement, float yaw, float pitch, InputButtons buttons, byte slot = 0)
        => (_movement, _yaw, _pitch, _buttons, _weaponSlot) = (movement, yaw, pitch, buttons, slot);

    public override void _Process(double delta)
    {
        FinishConnection();
        if (Client == null) return;
        if (_remoteTransport is { } remote)
        {
            remote.Pump();
            try { Client.Poll(); }
            catch (NetFormatException)
            {
                FailConnection("The server sent an invalid harbor packet.");
                return;
            }
            DrainEvents();
            if (!remote.IsConnected)
            {
                ConnectionStatus = HarborConnectionStatus.Disconnected;
                ConnectionMessage = "Disconnected from the harbor server.";
                return;
            }
            if (!HasPlayer && (_handshakeSeconds += delta) > 6)
            {
                FailConnection("The server did not complete the harbor handshake.");
                return;
            }
        }

        if (IsRemote || !IsPaused || !HasPlayer || _stepWhilePaused)
            _clock.Advance(delta, Tick);

        if (HasPlayer && ConnectionStatus == HarborConnectionStatus.Connecting)
        {
            ConnectionStatus = HarborConnectionStatus.Ready;
            ConnectionMessage = IsRemote ? "Connected to the harbor server." : "Local authority ready.";
        }
        UpdateVehicleView(delta);
    }

    private void Tick(double seconds)
    {
        if (!IsRemote && IsPaused && HasPlayer && !_stepWhilePaused) return;
        _stepWhilePaused = false;
        var hadPlayer = HasPlayer;
        _previousPosition = Client.Predicted.Position;
        _localNetwork?.Advance(seconds);
        Client.Poll();
        DrainEvents();
        if (HasPlayer)
        {
            var movement = IsPaused ? Vector2.Zero : _movement;
            Client.Tick(movement.X, movement.Y, _yaw, _pitch, IsPaused ? InputButtons.None : _buttons | _pulses, _weaponSlot);
            _pulses = InputButtons.None;
        }
        if (_authority != null)
        {
            _localNetwork!.Advance(0);
            _authority.Update(seconds);
            _localNetwork.Advance(0);
            Client.Poll();
            DrainEvents();
        }
        if (!hadPlayer && HasPlayer) _previousPosition = Client.Predicted.Position;
    }

    private void DrainEvents()
    {
        while (Client.Events.TryDequeue(out var message))
        {
            try
            {
                switch (message.Type)
                {
                    case HarborProtocol.State when HarborProtocol.Decode<HarborState>(message.Payload) is
                        { Lockdown: not null, PlayerCombat: not null, Enemies: not null, RemotePlayers: not null } state:
                        if (state.RunGeneration != _state.RunGeneration)
                        {
                            RunGeneration++;
                            _vehicleInitialized = false;
                            _weaponSlot = 0;
                        }
                        _state = state;
                        _vehicleTarget = state.Vehicle;
                        _enemies = state.Enemies.Select(s => ToActor(s, ActorKind.Npc)).ToArray();
                        _otherPlayers = state.RemotePlayers
                            .Where(s => s.Id != state.DriverId).Select(s => ToActor(s, ActorKind.Player)).ToArray();
                        break;
                    case HarborProtocol.Shot when HarborProtocol.Decode<ShotFired>(message.Payload) is { } shot:
                        ShotFired?.Invoke(shot);
                        break;
                    case HarborProtocol.Damage when HarborProtocol.Decode<ActorDamaged>(message.Payload) is { } damage:
                        ActorDamaged?.Invoke(damage);
                        break;
                    case HarborProtocol.Kill when HarborProtocol.Decode<ActorKilled>(message.Payload) is { } kill:
                        ActorKilled?.Invoke(kill);
                        break;
                }
            }
            catch (System.Text.Json.JsonException error)
            {
                GD.PushWarning($"Invalid harbor server state: {error.Message}");
            }
        }
    }

    private void UpdateVehicleView(double delta)
    {
        if (_authority is { } local)
        {
            var car = local.Vehicle;
            Vehicle.Restore(car.Position, car.Yaw, car.Speed, car.Steering, car.Distance);
            return;
        }
        if (_state.RunGeneration == 0) return;
        var carState = _vehicleTarget;
        var t = !_vehicleInitialized || (Vehicle.Position - carState.Position).Length > 12
            ? 1f : 1f - Mathf.Exp(-(float)delta * 18f);
        Vehicle.Restore(Vec3d.Lerp(Vehicle.Position, carState.Position, t), Mathf.LerpAngle((float)Vehicle.Yaw, (float)carState.Yaw, t),
            Mathf.Lerp((float)Vehicle.Speed, (float)carState.Speed, t),
            Mathf.Lerp((float)Vehicle.Steering, (float)carState.Steering, t),
            Mathf.Lerp((float)Vehicle.Distance, (float)carState.Distance, t));
        _vehicleInitialized = true;
    }

    private void ResetClientState()
    {
        RunGeneration++;
        Client = null!;
        _clock = new FixedTickLoop();
        _state = new HarborState();
        _enemies = [];
        _otherPlayers = [];
        _vehicleInitialized = false;
        _handshakeSeconds = 0;
        _pulses = InputButtons.None;
        Vehicle = new ArcadeVehicle(new Vec3d(_encounter.Vehicle[0], _encounter.Vehicle[1], _encounter.Vehicle[2]), tuning: _vehicleTuning);
        _stepWhilePaused = false;
    }

    private void CloseSession()
    {
        if (_connecting != null)
        {
            _connecting.Cancel();
            _connecting.Dispose();
        }
        _connecting = null;
        if (_pendingTransport is { } pending)
        {
            _ = pending.ContinueWith(completed =>
            {
                if (completed.IsCompletedSuccessfully) completed.Result.Dispose();
                else _ = completed.Exception;
            }, TaskScheduler.Default);
            _pendingTransport = null;
        }
        _remoteTransport?.Dispose();
        _remoteTransport = null;
        _authority = null;
        _localNetwork = null;
    }

    private void FailConnection(string message)
    {
        CloseSession();
        Client = null!;
        ConnectionStatus = HarborConnectionStatus.Failed;
        ConnectionMessage = message;
    }

    public Vec3d ResolveAim(Vec3d origin, Vec3d direction, out bool hasTarget)
    {
        var max = 90.0;
        if (Level.Raycast(origin, direction, max, out var block)) max = block.Distance;
        hasTarget = false;
        foreach (var actor in _enemies)
        {
            if (!actor.IsAlive || !Hitboxes.Raycast(origin, direction, max,
                new PoseSample(actor.Position, actor.Move.Crouched, true), out var distance, out _)) continue;
            max = distance;
            hasTarget = true;
        }
        return origin + direction * max;
    }

    private static Actor ToActor(HarborEnemyState state, ActorKind kind) => new(state.Id, kind)
    {
        Team = state.Team,
        Move = new MoveState { Position = state.Position, Velocity = state.Velocity, Yaw = state.Yaw,
            Crouched = (state.Flags & ActorFlags.Crouched) != 0 },
        Vitals = new Vitals { Health = state.Health, MaxHealth = state.MaxHealth },
        Status = state.Flags,
    };

    public static Vec3d ToCore(Vector3 position) => new(position.X, position.Y, position.Z);
    public static Vector3 ToGodot(Vec3d position) => new((float)position.X, (float)position.Y, (float)position.Z);
}
