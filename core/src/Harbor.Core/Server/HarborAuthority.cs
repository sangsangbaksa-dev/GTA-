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

public readonly record struct HarborVehicleState(Vec3d Position, double Yaw, double Speed, double Steering, double Distance);
public readonly record struct HarborEnemyState(int Id, int Team, Vec3d Position, Vec3d Velocity, double Yaw, int Health,
    int MaxHealth, ActorFlags Flags);

public sealed record HarborState
{
    public HarborLockdownState Lockdown { get; init; } = new();
    public HarborJobsState Jobs { get; init; } = new();
    public StreetPopulationState Ambient { get; init; } = new();
    public HarborPlayerCombat PlayerCombat { get; init; } = new();
    public HarborVehicleState Vehicle { get; init; }
    public HarborEnemyState[] Enemies { get; init; } = [];
    public int DriverId { get; init; } = -1;
    public int WantedLevel { get; init; }
    public CrimeStatus Crime { get; init; } = new();
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
    public ArcadeVehicle Vehicle { get; }
    public double? BestTime { get; private set; }

    private readonly Dictionary<int, int> _players = new();
    private long _lastPublishedTick = -1;
    private int _runGeneration = 1;

    public HarborAuthority(GameData combatData, HarborLockdownDef encounter, HarborLayout layout,
        ArcadeVehicleTuning vehicleTuning, ITransport transport, ILog log, int maxPlayers = 1,
        HarborJobCatalog? jobs = null, StreetPopulationDef? ambient = null)
    {
        Level = layout.CreateStaticWorld();
        Vehicle = new ArcadeVehicle(HarborLockdownDef.Point(encounter.Vehicle), 0, vehicleTuning);
        World = new SimWorld(log);
        Session = World.AddSystem(new ServerSession(transport,
            new ServerConfig { MaxPlayers = maxPlayers, SpawnPoint = layout.SpawnPosition }, staticWorld: Level));
        Combat = World.AddSystem(new CombatSystem(combatData.Combat, combatData.Materials, combatData.Weapons, Level, Session.History)
        {
            DefaultWeaponId = encounter.PlayerWeapon,
            AutoRespawn = false,
            UsesDownedState = (_, _) => false,
        });
        var heat = World.AddSystem(new HeatSystem(encounter.Heat));
        Lockdown = World.AddSystem(new HarborLockdown(encounter, Combat, Session, Level, heat, Vehicle));
        Jobs = World.AddSystem(new HarborJobs(jobs ?? new HarborJobCatalog(), Lockdown, Vehicle));
        Lockdown.Jobs = Jobs;
        if (ambient != null) Ambient = World.AddSystem(new StreetPopulation(ambient));
        Lockdown.Ambient = Ambient;
        Session.ShouldApplyMovement = actor => Lockdown.DriverId != actor.Id
            && Lockdown.Phase is not (LockdownPhase.Succeeded or LockdownPhase.Failed);
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
        if (_players.Count > 0 && Session.Tick - _lastPublishedTick >= 2) PublishState();
    }

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
            Phase = player is { IsAlive: false } ? LockdownPhase.Failed : Lockdown.Phase, RemainingEnemies = Lockdown.RemainingEnemies,
            WaveNumber = Lockdown.WaveNumber, WaveCount = Lockdown.WaveCount, Elapsed = Lockdown.Elapsed,
            ReinforcementSeconds = Lockdown.ReinforcementSeconds,
            StartPosition = Lockdown.StartPosition, StartRadius = Lockdown.StartRadius, CanStart = Lockdown.CanStart(actorId),
            ExtractionPosition = Lockdown.ExtractionPosition, ExtractionRadius = Lockdown.ExtractionRadius,
        };
        var enemies = new List<HarborEnemyState>(Lockdown.EnemyIds.Count);
        foreach (var id in Lockdown.EnemyIds)
            if (World.Actors.TryGet(id, out var a))
                enemies.Add(ActorState(a));
        return new HarborState
        {
            Lockdown = mission, Jobs = Jobs.GetState(actorId), Ambient = Ambient?.GetState(player?.Position) ?? new StreetPopulationState(),
            PlayerCombat = combat, WantedLevel = Lockdown.WantedLevel(actorId), Crime = Lockdown.CrimeOf(actorId),
            Vehicle = new HarborVehicleState(Vehicle.Position, Vehicle.Yaw, Vehicle.Speed, Vehicle.Steering, Vehicle.Distance),
            Enemies = enemies.ToArray(),
            DriverId = Lockdown.DriverId, IsDriving = Lockdown.DriverId == actorId,
            CanEnterVehicle = Lockdown.CanEnterVehicle(actorId), RunGeneration = _runGeneration, BestTime = BestTime,
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
