using System.Text.Json;
using Harbor.Core.Combat;
using Harbor.Core.Data;
using Harbor.Core.Geometry;
using Harbor.Core.Heat;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Ports;
using Harbor.Core.Sim;
using Harbor.Core.Vehicles;
using Harbor.Core.World;

namespace Harbor.Core.Server;

public sealed record HarborLockdownState
{
    public LockdownPhase Phase { get; init; }
    public int RemainingEnemies { get; init; }
    public int WaveNumber { get; init; }
    public int WaveCount { get; init; } = 2;
    public double Elapsed { get; init; }
    public double ReinforcementSeconds { get; init; }
    public Vec3d StartPosition { get; init; }
    public double StartRadius { get; init; }
    public bool CanStart { get; init; }
    public Vec3d ExtractionPosition { get; init; }
    public double ExtractionRadius { get; init; }
}

public sealed record HarborPlayerCombat(int Health = 100, int MaxHealth = 100, int Armor = 0, int MaxArmor = 100,
    string WeaponId = "", string WeaponName = "", int Magazine = 0, int Reserve = 0, bool IsReloading = false, int ActiveSlot = 0);

public readonly record struct HarborVehicleState(Vec3d Position, double Yaw, double Speed, double Steering, double Distance)
{
    public bool BlockedByObstacle { get; init; }
    public int Id { get; init; }
    public string ModelId { get; init; } = "";
    public string VehicleClass { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int Appearance { get; init; }
    public int DriverId { get; init; }
    public bool Occupied { get; init; }
    public double HalfWidth { get; init; }
    public double HalfLength { get; init; }
    public double RoofHeight { get; init; }
    public double Wheelbase { get; init; }
    public double WheelRadius { get; init; }
}

public sealed record HarborStreetAction(int VehicleId = -1, int PedestrianId = -1, bool VehicleOccupied = false,
    bool CanPickpocket = false, string CargoId = "");

public sealed record HarborStreetCrime(string Status = "none", long DirtyCash = 0, int Amount = 0, int Sequence = 0);
public readonly record struct HarborEnemyState(int Id, int Team, Vec3d Position, Vec3d Velocity, double Yaw, int Health,
    int MaxHealth, ActorFlags Flags);

public sealed record HarborState
{
    public HarborLockdownState Lockdown { get; init; } = new();
    public HarborJobsState Jobs { get; init; } = new();
    public StreetPopulationState Ambient { get; init; } = new();
    public HarborPlayerCombat PlayerCombat { get; init; } = new();
    public HarborVehicleState Vehicle { get; init; }
    public HarborVehicleState[] Vehicles { get; init; } = [];
    public HarborStreetAction StreetAction { get; init; } = new();
    public HarborStreetCrime StreetCrime { get; init; } = new();
    public HarborEnemyState[] Enemies { get; init; } = [];
    public int DriverId { get; init; } = -1;
    public int WantedLevel { get; init; }
    public int MaxWantedLevel { get; init; }
    public int PoliceUnits { get; init; }
    public bool PoliceHasContact { get; init; }
    public int PlayerVehicleId { get; init; } = -1;
    public bool IsDriving { get; init; }
    public bool CanEnterVehicle { get; init; }
    public int RunGeneration { get; init; }
    public double? BestTime { get; init; }
}

public static class HarborProtocol
{
    public const ushort State = 10100;
    public const ushort Shot = 10101;
    public const ushort Damage = 10102;
    public const ushort Kill = 10103;
    public const ushort Restart = 10200;

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json.Options);
    public static T? Decode<T>(ReadOnlySpan<byte> payload) => JsonSerializer.Deserialize<T>(payload, Json.Options);
}

/// <summary>The same engine-independent simulation host serves loopback and a dedicated transport.</summary>
public sealed class HarborAuthority
{
    public SimWorld World { get; }
    public ServerSession Session { get; }
    public CombatSystem Combat { get; }
    public HarborLockdown Lockdown { get; }
    public HarborJobs Jobs { get; }
    public StreetPopulation? Ambient { get; }
    public StaticWorld Level { get; }
    public ArcadeVehicle Vehicle => _fleet.Starter.Body;
    public double? BestTime { get; private set; }

    private readonly HarborFleet _fleet;
    private readonly bool _replicateState;
    private readonly Dictionary<int, int> _players = new();
    private long _lastPublishedTick = -1;
    private int _runGeneration = 1;

    public HarborAuthority(GameData combatData, HarborLockdownDef encounter, HarborLayout layout,
        ArcadeVehicleTuning vehicleTuning, ITransport transport, ILog log, int maxPlayers = 1,
        HarborJobCatalog? jobs = null, StreetPopulationDef? ambient = null, VehicleCatalog? vehicles = null, bool sandbox = false,
        bool replicateState = true)
    {
        _replicateState = replicateState;
        Level = layout.CreateStaticWorld();
        World = new SimWorld(log);
        Session = World.AddSystem(new ServerSession(transport,
            new ServerConfig { MaxPlayers = maxPlayers, SpawnPoint = layout.SpawnPosition }, staticWorld: Level));
        Combat = World.AddSystem(new CombatSystem(combatData.Combat, combatData.Materials, combatData.Weapons, Level, Session.History)
        {
            DefaultWeaponId = encounter.PlayerWeapon,
            AutoRespawn = sandbox,
            SpawnPointFor = _ => layout.SpawnPosition,
            UsesDownedState = (_, _) => false,
        });
        var heat = World.AddSystem(new HeatSystem(encounter.Heat));
        if (ambient != null) Ambient = World.AddSystem(new StreetPopulation(ambient, Level));
        _fleet = new HarborFleet(encounter, Level, World, vehicles ?? VehicleCatalog.Default(vehicleTuning),
            Ambient, ambient, vehicles != null);
        Lockdown = World.AddSystem(new HarborLockdown(encounter, Combat, Session, Level, heat, _fleet, Ambient, sandbox));
        Jobs = World.AddSystem(new HarborJobs(sandbox ? new HarborJobCatalog() : jobs ?? new HarborJobCatalog(), Lockdown));
        Lockdown.Jobs = Jobs;
        Session.ShouldApplyMovement = actor => !_fleet.IsDriving(actor.Id)
            && Lockdown.Phase is not (LockdownPhase.Succeeded or LockdownPhase.Failed);
        Session.ResolveMovementContacts = actor => _fleet.ResolvePedestrianContacts(actor, Session.MovementConfig);
        var events = World.Events;
        events.Subscribe<PlayerJoined>(OnJoined);
        events.Subscribe<PlayerLeft>(OnLeft);
        events.Subscribe<ClientRequest>(OnRequest);
        events.Subscribe<ShotFired>(e => Send(HarborProtocol.Shot, e));
        events.Subscribe<ActorDamaged>(e => Send(HarborProtocol.Damage, e));
        events.Subscribe<ActorKilled>(e => Send(HarborProtocol.Kill, e));
    }

    public void Update(double delta)
    {
        World.Update(delta);
        if (Lockdown.Phase == LockdownPhase.Succeeded && (BestTime is null || Lockdown.Elapsed < BestTime))
            BestTime = Lockdown.Elapsed;
        if (_replicateState && _players.Count > 0 && Session.Tick - _lastPublishedTick >= 2) PublishState();
    }

    public HarborVehicleState GetPlayerVehicle(int actorId) => (_fleet.Driving(actorId) ?? _fleet.Starter).State;

    public double CameraObstruction(int actorId, Vec3d origin, Vec3d direction, double distance) =>
        _fleet.CameraObstruction(actorId, origin, direction.Normalized(), Level.CameraObstruction(origin, direction, distance));

    public HarborState GetState(int actorId)
    {
        World.Actors.TryGet(actorId, out var player);
        var loadout = Combat.LoadoutOf(actorId);
        var slot = loadout?.Current;
        var combat = new HarborPlayerCombat(player?.Vitals.Health ?? Combat.Config.Health.Max,
            Combat.Config.Health.Max, player?.Vitals.Armor ?? 0, Combat.Config.Armor.Max,
            slot?.Def.Id ?? "", slot?.Def.DisplayName ?? "", slot?.InMagazine ?? 0,
            slot == null ? 0 : loadout!.ReserveOf(slot.Def.Class), loadout?.IsReloading ?? false, loadout?.Active ?? 0);
        var mission = new HarborLockdownState
        {
            Phase = !Lockdown.Sandbox && player is { IsAlive: false } ? LockdownPhase.Failed : Lockdown.Phase,
            RemainingEnemies = Lockdown.RemainingEnemies,
            WaveNumber = Lockdown.WaveNumber, WaveCount = Lockdown.WaveCount, Elapsed = Lockdown.Elapsed,
            ReinforcementSeconds = Lockdown.ReinforcementSeconds,
            StartPosition = Lockdown.StartPosition, StartRadius = Lockdown.StartRadius, CanStart = Lockdown.CanStart(actorId),
            ExtractionPosition = Lockdown.ExtractionPosition, ExtractionRadius = Lockdown.ExtractionRadius,
        };
        var enemies = new List<HarborEnemyState>(Lockdown.EnemyIds.Count);
        foreach (var id in Lockdown.EnemyIds)
            if (World.Actors.TryGet(id, out var a))
                enemies.Add(ActorState(a));
        var driving = _fleet.Driving(actorId);
        var action = Lockdown.GetStreetAction(actorId);
        return new HarborState
        {
            Lockdown = mission, Jobs = Jobs.GetState(actorId), Ambient = Ambient?.GetState(player?.Position) ?? new StreetPopulationState(),
            PlayerCombat = combat, WantedLevel = Lockdown.WantedLevel(actorId), MaxWantedLevel = Lockdown.MaxWantedLevel,
            PoliceUnits = Lockdown.PoliceUnits, PoliceHasContact = Lockdown.PoliceHasContact(actorId),
            StreetAction = action, StreetCrime = Lockdown.GetStreetCrime(actorId),
            Vehicle = (driving ?? _fleet.Starter).State, Vehicles = _fleet.GetStates(player?.Position),
            Enemies = enemies.ToArray(),
            DriverId = Lockdown.DriverId, IsDriving = driving != null, PlayerVehicleId = driving?.Id ?? -1,
            CanEnterVehicle = action.VehicleId >= 0 && driving == null, RunGeneration = _runGeneration, BestTime = BestTime,
        };
    }

    private static HarborEnemyState ActorState(Actor a) => new(a.Id, a.Team, a.Position, a.Move.Velocity,
        a.Move.Yaw, a.Vitals.Health, a.Vitals.MaxHealth, a.Flags);

    private void OnJoined(PlayerJoined joined)
    {
        _players[joined.Peer] = joined.ActorId;
        Session.SendEvent(joined.Peer, HarborProtocol.State, HarborProtocol.Encode(GetState(joined.ActorId)));
    }

    private void OnLeft(PlayerLeft left)
    {
        _players.Remove(left.Peer);
        if (_players.Count == 0) BestTime = null;
    }

    private void OnRequest(ClientRequest request)
    {
        if (request.Type != HarborProtocol.Restart || request.Payload.Length != 0
            || !_players.TryGetValue(request.Peer, out var actorId) || request.ActorId != actorId) return;

        if (_players.Count == 1 || Lockdown.Phase is LockdownPhase.Succeeded or LockdownPhase.Failed)
        {
            Lockdown.Reset();
            _runGeneration++;
        }
        else if (World.Actors.TryGet(actorId, out var actor) && !actor.IsAlive)
        {
            Lockdown.ResetPlayer(actorId);
        }
        PublishState();
    }

    private void PublishState()
    {
        _lastPublishedTick = Session.Tick;
        foreach (var (peer, actor) in _players)
            Session.SendEvent(peer, HarborProtocol.State, HarborProtocol.Encode(GetState(actor)));
    }

    private void Send<T>(ushort type, T value)
    {
        if (_players.Count > 0) Session.Broadcast(type, HarborProtocol.Encode(value));
    }
}
