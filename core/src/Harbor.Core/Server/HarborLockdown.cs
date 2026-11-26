using Harbor.Core.Combat;
using Harbor.Core.Data;
using Harbor.Core.Economy;
using Harbor.Core.Geometry;
using Harbor.Core.Heat;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Ports;
using Harbor.Core.Sim;
using Harbor.Core.Vehicles;
using Harbor.Core.World;
using M = System.Math;

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
    public double VehicleEntrySpeed { get; init; } = 4.5;
    public double VehicleExitSpeed { get; init; } = 3.5;
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
            && Positive(def.PoliceSpeed) && Positive(def.PoliceSightDistance)
            && Positive(def.VehicleEntrySpeed) && Positive(def.VehicleExitSpeed),
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
public sealed class HarborLockdown : ISimSystem
{
    private readonly HarborLockdownDef definition;
    private readonly CombatSystem combat;
    private readonly ServerSession session;
    private readonly StaticWorld level;
    private readonly HeatSystem heat;
    private readonly HarborFleet _fleet;
    private readonly StreetPopulation? _ambient;

    internal HarborLockdown(HarborLockdownDef definition, CombatSystem combat, ServerSession session, StaticWorld level,
        HeatSystem heat, HarborFleet fleet, StreetPopulation? ambient, bool sandbox)
    {
        this.definition = definition;
        this.combat = combat;
        this.session = session;
        this.level = level;
        this.heat = heat;
        _fleet = fleet;
        _ambient = ambient;
        Sandbox = sandbox;
        ExtractionPosition = HarborLockdownDef.Point(definition.Extraction);
        StartPosition = HarborLockdownDef.Point(definition.Start);
        _enemyMovement = new MovementConfig { WalkSpeed = definition.EnemySpeed };
        _policeMovement = new MovementConfig { WalkSpeed = definition.PoliceSpeed };
    }

    public bool Sandbox { get; }
    public LockdownPhase Phase { get; private set; } = LockdownPhase.FreeRoam;
    public int WaveNumber { get; private set; }
    public int WaveCount => 2;
    public double Elapsed { get; private set; }
    public double ReinforcementSeconds { get; private set; }
    public Vec3d ExtractionPosition { get; }
    public double ExtractionRadius => definition.ExtractionRadius;
    public Vec3d StartPosition { get; }
    public double StartRadius => definition.StartRadius;
    public int DriverId => _fleet.Starter.DriverId;
    internal HarborJobs? Jobs { get; set; }
    public int WantedLevel(int actorId) => heat.LevelOf(actorId);
    public int MaxWantedLevel => heat.Config.MaxLevel;
    public int PoliceUnits => _enemies.Count(enemy => enemy.Actor.Team == 3 && enemy.Actor.IsAlive);
    public bool IsDriving(int actorId) => _fleet.IsDriving(actorId);
    public double GetVehicleSpeed(int actorId) => _fleet.Driving(actorId)?.Body.Speed ?? 0;
    public bool CanStart(int actorId) => !Sandbox && Phase == LockdownPhase.FreeRoam && !IsDriving(actorId) && Jobs?.IsActive != true
        && PlayerIsNear(actorId, StartPosition, StartRadius);
    public bool CanEnterVehicle(int actorId) => !IsDriving(actorId) && GetStreetAction(actorId).VehicleId >= 0;
    public IReadOnlyList<int> EnemyIds => _enemyIds;
    public int RemainingEnemies => _enemies.Count(e => e.Actor.IsAlive && e.Actor.Team == 2);

    private readonly List<int> _enemyIds = new();
    private readonly List<Enemy> _enemies = new();
    private readonly MovementConfig _enemyMovement;
    private readonly MovementConfig _policeMovement;
    private SimWorld _world = null!;
    private double _policeDelay;
    private double _worldTime;
    private readonly Dictionary<int, InputButtons> _heldActions = new();
    private readonly Ledger _streetLedger = new();
    private readonly Dictionary<int, HarborStreetCrime> _streetResults = new();
    private int _streetSequence;
    private readonly Dictionary<int, Actor> _players = new();
    private int _policeTargetId = -1;

    private sealed class Enemy(Actor actor, byte slot, double delay)
    {
        public Actor Actor { get; } = actor;
        public byte Slot { get; } = slot;
        public double ShotDelay { get; set; } = delay;
        public double DetourSide { get; set; }
    }

    public void Initialize(SimWorld world)
    {
        _world = world;
        world.Events.Subscribe<PlayerJoined>(Join);
        world.Events.Subscribe<PlayerLeft>(Leave);
        world.Events.Subscribe<PlayerInputApplied>(ApplyPlayerInput);
        world.Events.Subscribe<ShotFired>(OnShot);
        world.Events.Subscribe<ActorDamaged>(OnDamage);
        world.Events.Subscribe<ActorKilled>(OnKilled);
        if (Sandbox) world.Events.Subscribe<ActorRespawned>(respawn => ResetPlayer(respawn.ActorId));
    }

    public void Step(double dt)
    {
        if (Phase is LockdownPhase.Succeeded or LockdownPhase.Failed || _players.Count == 0) return;
        _worldTime += dt;
        if (Phase != LockdownPhase.FreeRoam) Elapsed += dt;

        _fleet.Step(dt, session.Tick);
        if (!Sandbox && _players.Values.All(a => !a.IsAlive))
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
        if (!Sandbox && _players.Values.All(a => !a.IsAlive)) Phase = LockdownPhase.Failed;
    }

    public void Reset()
    {
        Jobs?.Cancel();
        foreach (var enemy in _enemies) RemoveEnemy(enemy.Actor.Id);
        _enemyIds.Clear();
        _enemies.Clear();
        _fleet.Reset();
        foreach (var player in _players.Values)
        {
            heat.Clear(player.Id);
            EquipPlayer(player);
        }
        _heldActions.Clear();
        _streetResults.Clear();
        _policeTargetId = -1;
        _policeDelay = 0;
        _worldTime = 0;
        Elapsed = 0;
        WaveNumber = 0;
        ReinforcementSeconds = 0;
        Phase = LockdownPhase.FreeRoam;
    }

    public void ResetPlayer(int actorId)
    {
        if (!_players.TryGetValue(actorId, out var actor)) return;
        _fleet.ClearDriver(actorId);
        heat.Clear(actorId);
        _heldActions.Remove(actorId);
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
        _fleet.ClearDriver(left.ActorId);
        _players.Remove(left.ActorId);
        _heldActions.Remove(left.ActorId);
        _streetResults.Remove(left.ActorId);
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
        var previous = _heldActions.GetValueOrDefault(actorId);
        var pressed = cmd.Buttons & ~previous;
        _heldActions[actorId] = cmd.Buttons;
        const InputButtons vehicleButtons = InputButtons.Vehicle | InputButtons.Interact;
        if ((pressed & vehicleButtons) != 0 && (previous & vehicleButtons) == 0)
        {
            var legacyInteract = !Sandbox && (pressed & InputButtons.Vehicle) == 0;
            if (IsDriving(actorId))
            {
                if (!legacyInteract || Jobs?.TryStart(actor) != true)
                {
                    if (_fleet.Exit(actor, definition.VehicleExitSpeed)) return;
                    RecordStreetResult(actorId, "exit_blocked");
                }
            }
            else if (!legacyInteract || Jobs?.TryRecover(actor) != true)
            {
                var (id, occupied) = EntryTarget(actor, starterOnly: legacyInteract);
                if (id >= 0 && _fleet.Enter(actor, id))
                {
                    if (occupied)
                    {
                        heat.Report(actorId, "vehicle_theft");
                        RecordStreetResult(actorId, "vehicle_taken");
                    }
                }
                else if (legacyInteract)
                {
                    if (CanStart(actorId)) StartEncounter();
                    else Jobs?.TryStart(actor);
                }
            }
        }

        if (_fleet.Driving(actorId) is { } vehicle)
        {
            vehicle.Input = new VehicleInput(cmd.Forward, cmd.Right, cmd.Has(InputButtons.Sprint));
            vehicle.InputTick = applied.ServerTick;
            return;
        }

        if ((pressed & InputButtons.Pickpocket) != 0 && Phase == LockdownPhase.FreeRoam)
            TryStreetTheft(actor, cmd);

        combat.ProcessInput(actorId, new CombatInput(cmd.Has(InputButtons.Fire), cmd.Has(InputButtons.Aim),
            cmd.Has(InputButtons.Reload), false, cmd.WeaponSlot, cmd.YawRadians, cmd.PitchRadians, applied.ViewTick),
            _world.Loop.StepSeconds);
    }

    private bool PlayerIsNear(int actorId, Vec3d point, double radius) =>
        _players.TryGetValue(actorId, out var actor) && actor.IsAlive && IsNear(actor, point, radius);

    private static bool IsNear(Actor actor, Vec3d point, double radius) =>
        (actor.Position - point).WithY(0).Length <= radius;

    public HarborStreetAction GetStreetAction(int actorId)
    {
        if (!_players.TryGetValue(actorId, out var actor) || !actor.IsAlive
            || Phase is LockdownPhase.Succeeded or LockdownPhase.Failed) return new HarborStreetAction();
        if (_fleet.Driving(actorId) is { } driven) return new HarborStreetAction(driven.Id);
        var (vehicleId, occupied) = EntryTarget(actor, starterOnly: false);
        var (pedestrian, cargo) = TheftTarget(actor.Position);
        return new HarborStreetAction(vehicleId, pedestrian, occupied, pedestrian >= 0, cargo);
    }

    public HarborStreetCrime GetStreetCrime(int actorId) =>
        _streetResults.GetValueOrDefault(actorId, new HarborStreetCrime(DirtyCash: _streetLedger.Balance(Wallet(actorId), Currency.Dirty)));

    private (int Id, bool Occupied) EntryTarget(Actor actor, bool starterOnly) =>
        Jobs?.CanDrive(actor.Id) == false ? (-1, false)
            : _fleet.EntryTarget(actor, definition.VehicleEnterRadius, definition.VehicleEntrySpeed, starterOnly);

    private (int Pedestrian, string Cargo) TheftTarget(Vec3d position)
    {
        if (Phase != LockdownPhase.FreeRoam || _ambient == null) return (-1, "");
        var pedestrian = _ambient.PickpocketTarget(position, level);
        return (pedestrian, pedestrian < 0 ? _ambient.CargoTarget(position, level) : "");
    }

    private void TryStreetTheft(Actor actor, InputCmd cmd)
    {
        var (target, cargo) = TheftTarget(actor.Position);
        if (cargo.Length > 0)
        {
            var cash = _ambient!.TakeCargo(cargo);
            if (cash > 0) PayStreetLoot(actor.Id, cash, "cargo_theft", "cargo_taken");
            return;
        }
        if (target < 0)
        {
            RecordStreetResult(actor.Id, "no_target");
            return;
        }
        var amount = _ambient!.Pickpocket(target, actor.Position, cmd.Has(InputButtons.Sprint) || cmd.Has(InputButtons.Aim));
        if (amount == 0)
        {
            heat.Report(actor.Id, "pickpocket_spotted");
            RecordStreetResult(actor.Id, "spotted");
            return;
        }
        PayStreetLoot(actor.Id, amount, "pickpocket", "pickpocketed");
    }

    private void PayStreetLoot(int actorId, int amount, string crime, string status)
    {
        var tx = new Transfer($"street:{_streetSequence + 1}", AccountId.Faucet("street"), Wallet(actorId), amount,
            Currency.Dirty, crime);
        _streetLedger.Post(tx);
        heat.Report(actorId, crime);
        RecordStreetResult(actorId, status, amount);
    }

    private static AccountId Wallet(int actorId) => AccountId.Wallet(actorId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void RecordStreetResult(int id, string status, int amount = 0) =>
        _streetResults[id] = new HarborStreetCrime(status, _streetLedger.Balance(Wallet(id), Currency.Dirty), amount, ++_streetSequence);

    private void OnKilled(ActorKilled death)
    {
        if (!_players.ContainsKey(death.VictimId)) return;
        _fleet.ClearDriver(death.VictimId);
        heat.Clear(death.VictimId);
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
        var targetId = player?.Id ?? -1;
        if (_policeTargetId != targetId)
        {
            _policeTargetId = targetId;
            ClearPolice();
            _policeDelay = 0;
        }
        if (player == null)
            return;

        var tier = heat.Config.Levels[WantedLevel(player.Id)];
        var officers = _enemies.Where(e => e.Actor.Team == 3).ToArray();
        foreach (var officer in officers.Where(e => !e.Actor.IsAlive))
            RemovePolice(officer);

        var deployed = officers.Where(e => e.Actor.IsAlive)
            .OrderBy(e => (e.Actor.Position - player.Position).LengthSquared).ToArray();
        foreach (var officer in deployed.Skip(tier.Units))
            RemovePolice(officer);

        var sight = definition.PoliceSightDistance * (1 + 0.15 * (tier.Level - 1));
        if (deployed.Take(tier.Units).Any(e => SeesPlayer(e.Actor.Position, player.Position, sight)))
            heat.KeepPursuit(player.Id);

        if (deployed.Length >= tier.Units)
        {
            _policeDelay = 0;
            return;
        }

        _policeDelay += dt;
        if (_policeDelay < tier.ResponseSeconds) return;
        _policeDelay = 0;
        var spawns = PoliceSpawnCandidates(player.Position)
            .Where(point => StaticPathOpen(point, point) && deployed.All(e => Vec3d.Distance(e.Actor.Position, point) > 3))
            .OrderBy(point => PoliceSpawnCost(point, player.Position)).ToArray();
        var count = tier.Units - deployed.Length;
        foreach (var (point, index) in spawns.Take(count).Select((p, i) => (p, i)))
            SpawnEnemy(point, 3, definition.EnemyStartDelay + index * 0.25);
    }

    private IEnumerable<Vec3d> PoliceSpawnCandidates(Vec3d player)
    {
        var authored = definition.PoliceSpawns.Select(HarborLockdownDef.Point).ToArray();
        foreach (var point in authored)
            if (Vec3d.Distance(point, player) >= 10)
                yield return point;
        if (!Sandbox || authored.Any(point => Vec3d.Distance(point, player) is >= 12 and <= 40)) yield break;

        // A fallback for open ground beyond the covered road spawns, at a distance the player can observe.
        var range = definition.PoliceSightDistance + 10;
        for (var i = 0; i < 8; i++)
        {
            var point = player.WithY(0) + Vec3d.FromYawPitch(i * M.PI / 4, 0) * range;
            if (StaticPathOpen(point, player.WithY(0))) yield return point;
        }
    }

    private bool SeesPlayer(Vec3d officer, Vec3d player, double sight) =>
        Vec3d.Distance(officer, player) < sight && StaticPathOpen(officer, player);

    private bool StaticPathOpen(Vec3d from, Vec3d to)
    {
        var delta = to - from;
        return !level.Raycast(from + new Vec3d(0, 1.25, 0), delta, delta.Length, out _);
    }

    private double PoliceSpawnCost(Vec3d spawn, Vec3d player)
    {
        var delta = player - spawn;
        var distance = delta.Length;
        var blocked = !StaticPathOpen(spawn, player);
        return M.Abs(distance - definition.PoliceSightDistance - 4) + (blocked ? 22 : 0);
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
            if (_enemies[i].Actor.Team == 3) RemovePolice(_enemies[i]);
        }
    }

    private void RemovePolice(Enemy officer)
    {
        RemoveEnemy(officer.Actor.Id);
        _enemyIds.Remove(officer.Actor.Id);
        _enemies.Remove(officer);
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
        var delta = player.Position - actor.Position;
        var distance = delta.WithY(0).Length;
        var yaw = DirectionYaw(delta);
        var police = actor.Team == 3;
        var holdDistance = police ? 7 : definition.EnemyHoldDistance;
        var movement = distance > holdDistance || police && !StaticPathOpen(actor.Position, player.Position) ? 1.0 : 0;
        if (police && movement > 0) yaw = PoliceHeading(enemy, yaw);
        Movement.Step(ref actor.Move, InputCmd.Create(0, 0, 0, movement, yaw, 0, InputButtons.None),
            dt, police ? _policeMovement : _enemyMovement, MoveModifiers.Default, level);

        enemy.ShotDelay -= dt;
        var origin = actor.Position + new Vec3d(0, combat.Config.EyeHeight, 0);
        var target = player.Position + new Vec3d(0, player.Move.Crouched ? 0.68 : 1.12, 0);
        var toTarget = target - origin;
        var tier = police ? WantedLevel(player.Id) : 0;
        var inRange = !police || !Sandbox || distance <= definition.PoliceSightDistance * (0.95 + 0.18 * tier);
        var canFire = inRange && enemy.ShotDelay <= 0 && !level.Raycast(origin, toTarget, toTarget.Length, out _);
        var pitch = System.Math.Atan2(toTarget.Y, toTarget.WithY(0).Length);
        var sway = M.Sin(_worldTime * 2.8 + actor.Id * 1.7) * 0.055;
        var loadout = combat.LoadoutOf(actor.Id)!;
        var reload = loadout.Current?.InMagazine == 0;
        if (canFire && !reload && !loadout.IsReloading)
            enemy.ShotDelay = definition.EnemyShotInterval * (police ? 1.2 / (1 + 0.15 * tier) : 1);
        combat.ProcessInput(actor.Id, new CombatInput(canFire && !reload, false, reload, false,
            enemy.Slot, DirectionYaw(toTarget) + sway, pitch, session.Tick), dt);
    }

    private double PoliceHeading(Enemy officer, double desired)
    {
        var position = officer.Actor.Position;
        bool Clear(double yaw) => StaticPathOpen(position, position + Vec3d.FromYawPitch(yaw, 0) * 2.4);
        if (Clear(desired))
        {
            officer.DetourSide = 0;
            return desired;
        }

        var side = officer.DetourSide == 0 ? (officer.Actor.Id % 2 == 0 ? -1 : 1) : officer.DetourSide;
        foreach (var angle in new[] { M.PI / 4, M.PI / 2, 3 * M.PI / 4 })
        {
            if (Clear(desired + angle * side))
            {
                officer.DetourSide = side;
                return desired + angle * side;
            }
            if (Clear(desired - angle * side))
            {
                officer.DetourSide = -side;
                return desired - angle * side;
            }
        }
        return desired;
    }

    private static double DirectionYaw(Vec3d delta) => System.Math.Atan2(-delta.X, -delta.Z);
}
