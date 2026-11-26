using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Harbor.Core.Combat;
using Harbor.Core.Data;
using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Server;
using Harbor.Core.Sim;
using Harbor.Core.Vehicles;
using Harbor.Core.World;
using Harbor.Game.Adapters;

namespace Harbor.Game.Bridge;

/// <summary>Runs the single-player authority and presentation client in the same process.</summary>
public partial class CoreHost : Node
{
    private const string LayoutPath = "data/world/harbor_front.json";

    public event Action<ShotFired>? ShotFired;
    public event Action<ActorDamaged>? ActorDamaged;
    public event Action<ActorKilled>? ActorKilled;

    public ClientSession Client { get; private set; } = null!;
    public StaticWorld Level { get; private set; } = null!;
    public HarborLayout Layout => _layout;
    public StreetPopulationState Ambient => _state.Ambient;
    public HarborPlayerCombat PlayerCombat => _state.PlayerCombat;
    public int WantedLevel => _state.WantedLevel;
    public int MaxWantedLevel => _state.MaxWantedLevel;
    public int PoliceUnits => _state.PoliceUnits;
    public bool IsDriving => _state.IsDriving;
    public int RunGeneration { get; private set; }
    public bool IsPaused { get; private set; }
    public HarborVehicleState Vehicle { get; private set; }
    public HarborVehicleState[] Vehicles => _state.Vehicles;
    public HarborStreetAction StreetAction => _state.StreetAction;
    public HarborStreetCrime StreetCrime => _state.StreetCrime;
    public IEnumerable<Actor> Enemies => _enemies;
    public bool HasPlayer => Client is { IsJoined: true } && Client.LocalServerState.Id == Client.LocalActorId;
    public Vec3d RenderPosition => IsDriving ? Vec3d.Lerp(_previousVehiclePosition, Vehicle.Position, _clock.Alpha)
        : HasPlayer ? Vec3d.Lerp(_previousPosition, Client.Predicted.Position, _clock.Alpha) : _layout.SpawnPosition;

    private HarborAuthority _authority = null!;
    private InMemoryNetwork _localNetwork = null!;
    private GameData _data = null!;
    private HarborLayout _layout = null!;
    private HarborLockdownDef _encounter = null!;
    private StreetPopulationDef _ambient = null!;
    private ArcadeVehicleTuning _vehicleTuning = null!;
    private VehicleCatalog _vehicles = null!;
    private FixedTickLoop _clock = new();
    private HarborState _state = new();
    private Actor[] _enemies = [];
    private Vec3d _previousPosition;
    private Vec3d _previousVehiclePosition;
    private Vector2 _movement;
    private float _yaw;
    private float _pitch;
    private InputButtons _buttons;
    private InputButtons _pulses;
    private byte _weaponSlot;

    public override void _Ready()
    {
        var files = new EmbeddedGameFiles();
        _layout = HarborLayout.Load(files, LayoutPath);
        Level = _layout.CreateStaticWorld();
        _data = GameData.LoadCombat(files);
        _encounter = HarborLockdownDef.Load(files, "data/missions/harbor_lockdown.json", _data.Weapons);
        _ambient = StreetPopulationDef.Load(files, "data/world/harbor_population.json");
        _vehicleTuning = ArcadeVehicleTuning.Load(files, "data/vehicles/calloway_compact.json");
        _vehicles = VehicleCatalog.Load(files, "data/vehicles", _vehicleTuning);

        StartLocalSession();
    }

    public void StartLocalSession()
    {
        RunGeneration++;
        _clock = new FixedTickLoop();
        _state = new HarborState();
        Vehicle = default;
        _previousVehiclePosition = default;
        _enemies = [];
        _pulses = InputButtons.None;
        _localNetwork = new InMemoryNetwork { LatencySeconds = 0 };
        var serverEndpoint = _localNetwork.CreateServer();
        _authority = new HarborAuthority(_data, _encounter, _layout, _vehicleTuning, serverEndpoint,
            new GodotLog(), maxPlayers: 1, ambient: _ambient, vehicles: _vehicles, sandbox: true);
        Client = new ClientSession(_localNetwork.Connect(serverEndpoint), "Rook", staticWorld: Level, interpolationDelayTicks: 0);
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
        if (!IsPaused || !HasPlayer) _clock.Advance(delta, Tick);
    }

    private void Tick(double seconds)
    {
        if (IsPaused && HasPlayer) return;
        var hadPlayer = HasPlayer;
        _previousPosition = Client.Predicted.Position;
        _localNetwork.Advance(seconds);
        Client.Poll();
        DrainEvents();
        if (HasPlayer)
        {
            var movement = IsPaused ? Vector2.Zero : _movement;
            Client.Tick(movement.X, movement.Y, _yaw, _pitch, IsPaused ? InputButtons.None : _buttons | _pulses, _weaponSlot);
            _pulses = InputButtons.None;
        }
        _localNetwork.Advance(0);
        _authority.Update(seconds);
        _localNetwork.Advance(0);
        Client.Poll();
        DrainEvents();
        if (!hadPlayer && HasPlayer) _previousPosition = Client.Predicted.Position;
        if (HasPlayer)
        {
            var previous = Vehicle;
            Vehicle = _authority.GetPlayerVehicle(Client.LocalActorId);
            _previousVehiclePosition = previous.Id == Vehicle.Id ? previous.Position : Vehicle.Position;
        }
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
                        { Ambient.Pedestrians: not null, Ambient.Traffic: not null, PlayerCombat: not null,
                            Enemies: not null, Vehicles: not null, StreetAction: not null, StreetCrime: not null } state:
                        if (state.RunGeneration != _state.RunGeneration)
                        {
                            RunGeneration++;
                            _weaponSlot = 0;
                        }
                        _state = state;
                        _enemies = state.Enemies.Select(ToActor).ToArray();
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

    private static Actor ToActor(HarborEnemyState state) => new(state.Id, ActorKind.Npc)
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
