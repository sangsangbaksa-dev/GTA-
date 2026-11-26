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

public enum LockdownPhase { FreeRoam, FirstWave, Reinforcements, Extraction, Succeeded, Failed }

public sealed record HarborLockdownDef
{
    public string PlayerWeapon { get; init; } = "";
    public string PlayerSecondary { get; init; } = "";
    public string EnemyWeapon { get; init; } = "";
    public int InitialArmor { get; init; }
    public int PlayerReserveMagazines { get; init; }
    public int EnemyHealth { get; init; }
    public double EnemySpeed { get; init; }
    public double EnemyShotInterval { get; init; }
    public double EnemyStartDelay { get; init; }
    public double EnemyHoldDistance { get; init; }
    public double ReinforcementDelay { get; init; }
    public double[] Start { get; init; } = [];
    public double StartRadius { get; init; }
    public double[] Vehicle { get; init; } = [];
    public double VehicleEnterRadius { get; init; }
    public double[][] PoliceSpawns { get; init; } = [];
    public double PoliceSpeed { get; init; }
    public double PoliceSightDistance { get; init; }
    public HeatConfig Heat { get; init; } = new();
    public double[][] FirstWave { get; init; } = [];
    public double[][] Reinforcements { get; init; } = [];
    public double[] Extraction { get; init; } = [];
    public double ExtractionRadius { get; init; }

    public static HarborLockdownDef Load(IFileSystem files, string path, IReadOnlyDictionary<string, WeaponDef> weapons)
    {
        var def = Json.Read<HarborLockdownDef>(files, path);
        Json.Require(!string.IsNullOrEmpty(def.PlayerWeapon) && weapons.ContainsKey(def.PlayerWeapon)
            && !string.IsNullOrEmpty(def.PlayerSecondary) && weapons.ContainsKey(def.PlayerSecondary)
            && def.PlayerWeapon != def.PlayerSecondary
            && !string.IsNullOrEmpty(def.EnemyWeapon) && weapons.ContainsKey(def.EnemyWeapon),
            $"{path}: missing or invalid weapon ids");
        Json.Require(def.InitialArmor >= 0 && def.PlayerReserveMagazines >= 1 && def.EnemyHealth is > 0 and <= 100
            && Positive(def.EnemySpeed) && Positive(def.EnemyShotInterval) && Positive(def.EnemyStartDelay)
            && Positive(def.EnemyHoldDistance) && Positive(def.ReinforcementDelay),
            $"{path}: invalid encounter tuning");
        Json.Require(ValidWave(def.FirstWave) && ValidWave(def.Reinforcements) && ValidPoint(def.Extraction)
            && Positive(def.ExtractionRadius), $"{path}: invalid wave or extraction location");
        Json.Require(ValidPoint(def.Start) && Positive(def.StartRadius) && ValidPoint(def.Vehicle)
            && Positive(def.VehicleEnterRadius) && ValidWave(def.PoliceSpawns)
            && Positive(def.PoliceSpeed) && Positive(def.PoliceSightDistance),
            $"{path}: invalid start, vehicle, or police tuning");
        def.Heat.Validate();
        return def;
    }

    internal static Vec3d Point(double[] point) => new(point[0], point[1], point[2]);
    private static bool ValidWave(double[][]? wave) => wave is { Length: > 0 } && wave.All(ValidPoint);
    private static bool ValidPoint(double[]? point) => point is { Length: 3 } && point.All(double.IsFinite);
    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
}

/// <summary>Authoritative local encounter built on the same shared movement, damage, ammo and events as the game server.</summary>
public sealed class HarborLockdown(HarborLockdownDef definition, CombatSystem combat, ServerSession session,
    StaticWorld level, HeatSystem heat, ArcadeVehicle vehicle) : ISimSystem
{
    public LockdownPhase Phase { get; private set; } = LockdownPhase.FreeRoam;
    public int WaveNumber { get; private set; }
    public int WaveCount => 2;
    public double Elapsed { get; private set; }
    public double ReinforcementSeconds { get; private set; }
    public Vec3d ExtractionPosition { get; } = HarborLockdownDef.Point(definition.Extraction);
    public double ExtractionRadius => definition.ExtractionRadius;
    public Vec3d StartPosition { get; } = HarborLockdownDef.Point(definition.Start);
    public double StartRadius => definition.StartRadius;
    public int DriverId { get; private set; } = -1;
    internal HarborJobs? Jobs { get; set; }
    internal StreetPopulation? Ambient { get; set; }
    private readonly StreetCrimeState _crime = new();
    public CrimeStatus CrimeOf(int actorId) => _crime.Get(actorId);
    public int WantedLevel(int actorId) => heat.LevelOf(actorId);
    public bool CanStart(int actorId) => Phase == LockdownPhase.FreeRoam && DriverId != actorId && Jobs?.IsActive != true
        && PlayerIsNear(actorId, StartPosition, StartRadius);
    public bool CanEnterVehicle(int actorId) => DriverId < 0 && Jobs?.CanDrive(actorId) != false
        && PlayerIsNear(actorId, vehicle.Position, definition.VehicleEnterRadius);
    public IReadOnlyList<int> EnemyIds => _enemyIds;
    public int RemainingEnemies => _enemies.Count(e => e.Actor.IsAlive && e.Actor.Team == 2);

    private readonly List<int> _enemyIds = new();
    private readonly List<Enemy> _enemies = new();
    private readonly MovementConfig _enemyMovement = new() { WalkSpeed = definition.EnemySpeed };
    private readonly MovementConfig _policeMovement = new() { WalkSpeed = definition.PoliceSpeed };
    private SimWorld _world = null!;
    private double _policeDelay;
    private readonly HashSet<int> _interactHeld = new();
    private VehicleInput _vehicleInput;
    private long _vehicleInputTick;
    private readonly Dictionary<int, Actor> _players = new();
    private int _policeTargetId = -1;

    private sealed class Enemy(Actor actor, byte slot, double delay)
    {
        public Actor Actor { get; } = actor;
        public byte Slot { get; } = slot;
        public double ShotDelay { get; set; } = delay;
        public Vec3d LastKnown { get; set; } = actor.Position;
    }

    public void Initialize(SimWorld world)
    {
        _world = world;
        world.Events.Subscribe<PlayerJoined>(Join);
        world.Events.Subscribe<PlayerLeft>(Leave);
        world.Events.Subscribe<PlayerInputApplied>(ApplyPlayerInput);
        world.Events.Subscribe<ShotFired>(OnShot);
        world.Events.Subscribe<ActorDamaged>(OnDamage);
    }

    public void Step(double dt)
    {
        if (Phase is LockdownPhase.Succeeded or LockdownPhase.Failed || _players.Count == 0) return;
        if (Phase != LockdownPhase.FreeRoam) Elapsed += dt;

        if (DriverId >= 0 && (!_players.TryGetValue(DriverId, out var driver) || !driver.IsAlive))
            ClearDriver();
        if (DriverId >= 0 || System.Math.Abs(vehicle.Speed) > 0.01)
        {
            vehicle.Step(DriverId >= 0 && session.Tick - _vehicleInputTick <= 6 ? _vehicleInput : default, dt, level);
            if (_players.TryGetValue(DriverId, out var actor))
            {
                actor.Position = vehicle.Position;
                actor.Move.Velocity = Vec3d.FromYawPitch(vehicle.Yaw, 0) * vehicle.Speed;
            }
        }

        if (_players.Values.All(a => !a.IsAlive))
        {
            Phase = LockdownPhase.Failed;
            return;
        }

        switch (Phase)
        {
            case LockdownPhase.FreeRoam:
                if (Jobs?.IsCombat != true) TickPolice(dt);
                break;
            case LockdownPhase.FirstWave when RemainingEnemies == 0:
                Phase = LockdownPhase.Reinforcements;
                WaveNumber = 2;
                ReinforcementSeconds = definition.ReinforcementDelay;
                foreach (var survivor in _players.Values.Where(a => a.IsAlive))
                    combat.AddArmor(survivor, definition.InitialArmor / 2);
                return;
            case LockdownPhase.Reinforcements when ReinforcementSeconds > 0:
                ReinforcementSeconds = System.Math.Max(0, ReinforcementSeconds - dt);
                if (ReinforcementSeconds == 0) SpawnWave(definition.Reinforcements);
                return;
            case LockdownPhase.Reinforcements when RemainingEnemies == 0:
                Phase = LockdownPhase.Extraction;
                return;
            case LockdownPhase.Extraction:
                if (_players.Values.Any(a => a.IsAlive && IsNear(a, ExtractionPosition, ExtractionRadius)))
                    Phase = LockdownPhase.Succeeded;
                return;
        }

        foreach (var enemy in _enemies)
        {
            if (!enemy.Actor.IsAlive) continue;
            var target = enemy.Actor.Team == 3 ? _players.GetValueOrDefault(_policeTargetId) :
                _players.Values.Where(a => a.IsAlive && (Jobs?.IsCombat != true || Jobs.IsParticipant(a.Id)))
                    .MinBy(a => (a.Position - enemy.Actor.Position).LengthSquared);
            if (target is { IsAlive: true }) TickEnemy(enemy, target, dt);
        }
        if (_players.Values.All(a => !a.IsAlive)) Phase = LockdownPhase.Failed;
    }

    public void Reset()
    {
        Jobs?.Cancel();
        foreach (var enemy in _enemies) RemoveEnemy(enemy.Actor.Id);
        _enemyIds.Clear();
        _enemies.Clear();
        ClearDriver();
        vehicle.Restore(HarborLockdownDef.Point(definition.Vehicle), 0, 0, 0, 0);
        foreach (var player in _players.Values)
        {
            heat.Clear(player.Id);
            EquipPlayer(player);
        }
        _interactHeld.Clear();
        _crime.Clear();
        Ambient?.ResetCrimes();
        _policeTargetId = -1;
        _policeDelay = 0;
        Elapsed = 0;
        WaveNumber = 0;
        ReinforcementSeconds = 0;
        Phase = LockdownPhase.FreeRoam;
    }

    public void ResetPlayer(int actorId)
    {
        if (!_players.TryGetValue(actorId, out var actor)) return;
        if (DriverId == actorId) ClearDriver();
        heat.Clear(actorId);
        _interactHeld.Remove(actorId);
        _crime.Remove(actorId);
        EquipPlayer(actor);
    }

    private void Join(PlayerJoined joined)
    {
        var actor = _world.Actors.Get(joined.ActorId);
        _players[actor.Id] = actor;
        EquipPlayer(actor);
    }

    private void Leave(PlayerLeft left)
    {
        if (DriverId == left.ActorId) ClearDriver();
        _players.Remove(left.ActorId);
        _interactHeld.Remove(left.ActorId);
        _crime.Remove(left.ActorId);
        heat.Clear(left.ActorId);
        combat.Unregister(left.ActorId);
        if (_players.Count == 0) Reset();
    }

    private void EquipPlayer(Actor actor)
    {
        var spawnSlot = _players.Keys.Order().ToList().IndexOf(actor.Id);
        actor.Move = new MoveState { Position = session.Config.SpawnPoint + new Vec3d(spawnSlot * 1.5, 0, 0) };
        actor.Status = ActorFlags.None;
        actor.Team = 1;
        combat.Unregister(actor.Id);
        var loadout = combat.Register(actor);
        combat.GiveWeapon(actor.Id, definition.PlayerSecondary);
        foreach (var slot in loadout.Slots)
            loadout.Reserve[slot.Def.Class] = slot.Def.MagazineSize * definition.PlayerReserveMagazines;
        combat.AddArmor(actor, definition.InitialArmor);
    }

    private void ApplyPlayerInput(PlayerInputApplied applied)
    {
        var actorId = applied.ActorId;
        if (Phase is LockdownPhase.Succeeded or LockdownPhase.Failed
            || !_players.TryGetValue(actorId, out var actor) || !actor.IsAlive) return;
        var cmd = applied.Cmd;
        if (Phase == LockdownPhase.FreeRoam && Jobs?.IsActive != true && DriverId != actorId)
            HandleStreetCrime(actor, cmd, applied.ServerTick);
        if (!cmd.Has(InputButtons.Interact))
        {
            _interactHeld.Remove(actorId);
        }
        else if (_interactHeld.Add(actorId))
        {
            if (DriverId == actorId)
            {
                if (Jobs?.TryStart(actor) != true) TryExitVehicle(actor);
            }
            else if (Jobs?.TryRecover(actor) != true)
            {
                if (CanEnterVehicle(actorId)) DriverId = actorId;
                else if (CanStart(actorId)) StartEncounter();
                else Jobs?.TryStart(actor);
            }
        }

        if (DriverId == actorId)
        {
            _vehicleInput = new VehicleInput(cmd.Forward, cmd.Right, cmd.Has(InputButtons.Sprint));
            _vehicleInputTick = applied.ServerTick;
            return;
        }
        combat.ProcessInput(actorId, new CombatInput(cmd.Has(InputButtons.Fire), cmd.Has(InputButtons.Aim),
            cmd.Has(InputButtons.Reload), false, cmd.WeaponSlot, cmd.YawRadians, cmd.PitchRadians, applied.ViewTick),
            _world.Loop.StepSeconds);
    }

    private void HandleStreetCrime(Actor actor, InputCmd cmd, long tick)
    {
        if (Ambient == null) return;
        var action = cmd.Buttons & (InputButtons.Pickpocket | InputButtons.Melee | InputButtons.Carjack);
        if (!_crime.TryAct(actor.Id, action, tick, _world.Loop.StepSeconds)) return;
        bool Reachable(Vec3d p)
        {
            var origin = actor.Position + new Vec3d(0, 1, 0);
            var d = p + new Vec3d(0, 1, 0) - origin;
            return d.Length < 0.01 || !level.Raycast(origin, d, d.Length, out _);
        }
        if (action.HasFlag(InputButtons.Carjack))
        {
            if (DriverId >= 0 || !Ambient.TryTakeTraffic(actor, Reachable, out var car))
            { _crime.Record(actor.Id, "NO AVAILABLE VEHICLE", 0); return; }
            vehicle.Restore(car.Position, car.Yaw, 0, 0, 0);
            DriverId = actor.Id;
            heat.Report(actor.Id, "vehicle_theft");
            _crime.Record(actor.Id, "VEHICLE STOLEN", 0);
            return;
        }
        var assault = action.HasFlag(InputButtons.Melee);
        if (!Ambient.TryStreetCrime(actor, assault, Reachable, out var cash, out var reported, out var counter))
        { _crime.Record(actor.Id, "NO AVAILABLE TARGET", 0); return; }
        if (reported) heat.Report(actor.Id, assault ? "assault" : "pickpocket");
        if (counter) combat.ApplyDamage(actor, null, 9, HitZone.Torso, false, "civilian_counter");
        _crime.Record(actor.Id, assault ? (counter ? "CIVILIAN FOUGHT BACK" : "ASSAULT REPORTED") :
            reported ? "THEFT REPORTED" : $"WALLET +${cash}", cash);
    }

    private bool PlayerIsNear(int actorId, Vec3d point, double radius) =>
        _players.TryGetValue(actorId, out var actor) && actor.IsAlive && IsNear(actor, point, radius);

    private static bool IsNear(Actor actor, Vec3d point, double radius) =>
        (actor.Position - point).WithY(0).Length <= radius;

    private void TryExitVehicle(Actor player)
    {
        if (System.Math.Abs(vehicle.Speed) > 3.5) return;
        var forward = Vec3d.FromYawPitch(vehicle.Yaw, 0);
        var right = new Vec3d(-forward.Z, 0, forward.X);
        var probeSize = new Vec3d(0.8, 1.8, 0.8);
        foreach (var offset in new[] { right * 1.7, right * -1.7, forward * -2.5 })
        {
            var position = vehicle.Position + offset;
            if (level.Overlapping(Aabb.FromCenter(position + new Vec3d(0, 0.9, 0), probeSize)).Any()) continue;
            ClearDriver();
            player.Position = position;
            player.Move.Velocity = Vec3d.Zero;
            return;
        }
    }

    private void ClearDriver()
    {
        DriverId = -1;
        _vehicleInput = default;
    }

    private void StartEncounter()
    {
        Phase = LockdownPhase.FirstWave;
        WaveNumber = 1;
        foreach (var id in _players.Keys) heat.Clear(id);
        ClearPolice();
        SpawnWave(definition.FirstWave);
    }

    private void OnShot(ShotFired shot)
    {
        if (Phase == LockdownPhase.FreeRoam && _players.ContainsKey(shot.ShooterId)
            && (Jobs?.IsCombat != true || !Jobs.IsParticipant(shot.ShooterId)))
            heat.Report(shot.ShooterId, "shots_fired");
    }

    private void OnDamage(ActorDamaged damage)
    {
        if (Phase == LockdownPhase.FreeRoam && _players.ContainsKey(damage.AttackerId)
            && _world.Actors.TryGet(damage.VictimId, out var actor) && actor.Team == 3)
            heat.Report(damage.AttackerId, "attack_police");
    }

    private void TickPolice(double dt)
    {
        if (!_players.TryGetValue(_policeTargetId, out var player) || !player.IsAlive || WantedLevel(player.Id) == 0)
            player = _players.Values.Where(a => a.IsAlive && WantedLevel(a.Id) > 0).MaxBy(a => WantedLevel(a.Id));
        _policeTargetId = player?.Id ?? -1;
        if (player == null)
        {
            ClearPolice();
            _policeDelay = 0;
            return;
        }

        var tier = heat.Config.Levels[WantedLevel(player.Id)];
        // Retire dead units and surplus officers; keep the roster bounded.
        var officers = _enemies.Where(e => e.Actor.Team == 3).ToList();
        foreach (var officer in officers.Where(e => !e.Actor.IsAlive)
                     .Concat(officers.Where(e => e.Actor.IsAlive).Skip(tier.Units)).ToArray())
        {
            RemoveEnemy(officer.Actor.Id);
            _enemyIds.Remove(officer.Actor.Id);
            _enemies.Remove(officer);
        }
        officers = _enemies.Where(e => e.Actor.Team == 3).ToList();
        foreach (var officer in officers)
        {
            var from = officer.Actor.Position + new Vec3d(0, 1.5, 0);
            var delta = player.Position + new Vec3d(0, 1.25, 0) - from;
            if (delta.Length < definition.PoliceSightDistance && !level.Raycast(from, delta, delta.Length, out _))
            {
                heat.KeepPursuit(player.Id);
                officer.LastKnown = player.Position;
            }
        }
        if (officers.Count >= tier.Units) { _policeDelay = 0; return; }
        _policeDelay += dt;
        if (_policeDelay < tier.ResponseSeconds) return;
        _policeDelay = 0;
        var points = definition.PoliceSpawns.Select(HarborLockdownDef.Point);
        var spawn = points.OrderBy(p => PoliceSpawnCost(p, player.Position))
            .Where(p => officers.All(o => (o.Actor.Position - p).LengthSquared > 9))
            .Select(p => (Vec3d?)p).FirstOrDefault();
        if (spawn is not { } position) return;
        SpawnEnemy(position, 3, definition.EnemyStartDelay);
        _enemies[^1].LastKnown = player.Position;
    }

    private double PoliceSpawnCost(Vec3d spawn, Vec3d player)
    {
        var delta = player - spawn;
        var distance = delta.Length;
        var blocked = level.Raycast(spawn + new Vec3d(0, 1.25, 0), delta, distance, out _);
        return System.Math.Abs(distance - definition.PoliceSightDistance - 14) + (blocked ? 0 : 22);
    }

    private void RemoveEnemy(int id)
    {
        combat.Unregister(id);
        _world.Actors.Remove(id);
    }

    private void ClearPolice()
    {
        for (var i = _enemies.Count - 1; i >= 0; i--)
        {
            var actor = _enemies[i].Actor;
            if (actor.Team != 3) continue;
            RemoveEnemy(actor.Id);
            _enemyIds.Remove(actor.Id);
            _enemies.RemoveAt(i);
        }
    }

    private void SpawnWave(double[][] points)
    {
        foreach (var point in points)
            SpawnEnemy(HarborLockdownDef.Point(point), team: 2, definition.EnemyStartDelay + (_enemies.Count % 3) * 0.55);
    }

    internal void PrepareJobCombat()
    {
        foreach (var id in _players.Keys.Where(id => Jobs?.IsParticipant(id) == true)) heat.Clear(id);
        ClearPolice();
    }

    internal int[] SpawnJobEnemies(double[][] points) => points.Select((point, index) =>
        SpawnEnemy(HarborLockdownDef.Point(point), 2, definition.EnemyStartDelay + index * 0.45)).ToArray();

    internal void RemoveJobEnemies(IReadOnlyCollection<int> ids)
    {
        foreach (var id in ids)
        {
            if (!_enemyIds.Remove(id)) continue;
            _enemies.RemoveAll(enemy => enemy.Actor.Id == id);
            RemoveEnemy(id);
        }
    }

    private int SpawnEnemy(Vec3d point, int team, double delay)
    {
        var actor = _world.Actors.Spawn(ActorKind.Npc, point);
        actor.Team = team;
        var loadout = combat.Register(actor);
        actor.Vitals = Vitals.Full(definition.EnemyHealth);
        combat.GiveWeapon(actor.Id, definition.EnemyWeapon);
        var slot = loadout.Slots.FindIndex(s => s.Def.Id == definition.EnemyWeapon);
        loadout.Active = slot;
        loadout.Reserve[loadout.Slots[slot].Def.Class] = 60;
        _enemies.Add(new Enemy(actor, (byte)slot, delay));
        _enemyIds.Add(actor.Id);
        return actor.Id;
    }

    private void TickEnemy(Enemy enemy, Actor player, double dt)
    {
        var actor = enemy.Actor;
        var police = actor.Team == 3;
        var delta = (police ? enemy.LastKnown : player.Position) - actor.Position;
        var distance = delta.WithY(0).Length;
        var yaw = DirectionYaw(delta);
        var holdDistance = police ? 7 : definition.EnemyHoldDistance;
        var movement = distance > holdDistance ? 1.0 : 0;
        Movement.Step(ref actor.Move, InputCmd.Create(0, 0, 0, movement, yaw, 0, InputButtons.None),
            dt, police ? _policeMovement : _enemyMovement, MoveModifiers.Default, level);

        enemy.ShotDelay -= dt;
        var origin = actor.Position + new Vec3d(0, combat.Config.EyeHeight, 0);
        var target = player.Position + new Vec3d(0, player.Move.Crouched ? 0.68 : 1.12, 0);
        var toTarget = target - origin;
        var canFire = enemy.ShotDelay <= 0 && (!police || toTarget.Length < definition.PoliceSightDistance)
            && !level.Raycast(origin, toTarget, toTarget.Length, out _);
        var pitch = System.Math.Atan2(toTarget.Y, toTarget.WithY(0).Length);
        var sway = System.Math.Sin(Elapsed * 2.8 + actor.Id * 1.7) * 0.055;
        var loadout = combat.LoadoutOf(actor.Id)!;
        var reload = loadout.Current?.InMagazine == 0;
        if (canFire && !reload && !loadout.IsReloading) enemy.ShotDelay = definition.EnemyShotInterval;
        combat.ProcessInput(actor.Id, new CombatInput(canFire && !reload, false, reload, false,
            enemy.Slot, DirectionYaw(toTarget) + sway, pitch, session.Tick), dt);
    }

    private static double DirectionYaw(Vec3d delta) => System.Math.Atan2(-delta.X, -delta.Z);
}
