using Harbor.Core.Combat;
using Harbor.Core.Geometry;
using Harbor.Core.Data;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Ports;
using Harbor.Core.Server;
using Harbor.Core.Sim;
using Harbor.Core.Vehicles;
using Harbor.Core.World;
using Xunit;

namespace Harbor.Core.Tests.Server;

public sealed class HarborAuthorityTests
{
    [Fact]
    public void SharedJobAndStreetPopulationFitTheBoundedServerEvent()
    {
        var game = new Harness(includeAmbient: true);
        var player = game.Join("Rook");
        var job = game.Catalog.Jobs.Single(j => j.Id == "stackyard_sweep");
        player.Actor.Position = job.StartPosition;
        game.PressInteract(player);

        Assert.NotEmpty(player.State.Ambient.Pedestrians);
        Assert.Equal(job.Id, player.State.Jobs.Active?.Id);
        Assert.InRange(HarborProtocol.Encode(player.State).Length, 1, Messages.MaxGameEventPayload);
    }

    [Fact]
    public void DistrictCombatJobRunsSharedWavesAndPaysOncePerParticipantOnReplay()
    {
        var game = new Harness();
        var owner = game.Join("Rook");
        var partner = game.Join("Partner");
        var outsider = game.Join("Observer");
        var job = game.Catalog.Jobs.Single(j => j.Id == "stackyard_sweep");
        owner.Actor.Position = job.StartPosition;
        partner.Actor.Position = job.StartPosition + new Vec3d(4, 0, 0);
        game.PressInteract(owner);

        Assert.Equal(2, owner.State.Jobs.Active!.RemainingEnemies);
        Assert.True(partner.State.Jobs.Active!.IsParticipant);
        Assert.False(outsider.State.Jobs.Active!.IsParticipant);
        Assert.Equal(LockdownPhase.FreeRoam, owner.State.Lockdown.Phase);
        game.ClearWave(owner.Actor);
        Assert.Equal(2, partner.State.Jobs.Active!.Stage);
        Assert.Equal(1, partner.State.Jobs.Active.RemainingEnemies);
        game.ClearWave(owner.Actor);
        Assert.Equal(3, owner.State.Jobs.Active!.Stage);
        partner.Actor.Position = job.Stages[^1].Position;
        game.Ticks(5);

        Assert.Equal(HarborJobStatus.Succeeded, owner.State.Jobs.LastResult!.Status);
        Assert.Equal(job.Reward, owner.State.Jobs.Credits);
        Assert.Equal(job.Reward, partner.State.Jobs.Credits);
        Assert.Equal(0, outsider.State.Jobs.Credits);
        Assert.Null(outsider.State.Jobs.LastResult);
        Assert.Empty(game.Authority.Lockdown.EnemyIds);
        var firstRun = owner.State.Jobs.LastResult.RunId;
        game.Ticks(45);
        Assert.Equal(job.Reward, owner.State.Jobs.Credits);

        owner.Actor.Position = job.StartPosition;
        game.PressInteract(owner);
        Assert.True(owner.State.Jobs.Active!.RunId > firstRun);
        Assert.Null(owner.State.Jobs.LastResult);
        Assert.Equal(job.Reward, owner.State.Jobs.Credits);
    }

    [Fact]
    public void RecoveryRequiresAnExplicitParticipantInteraction()
    {
        var game = new Harness();
        var owner = game.Join("Rook");
        var outsider = game.Join("Observer");
        var job = game.Catalog.Jobs.Single(j => j.Id == "quay_recovery");
        owner.Actor.Position = job.StartPosition;
        game.PressInteract(owner);
        game.ClearWave(owner.Actor);

        outsider.Actor.Position = job.Stages[^1].Position;
        game.PressInteract(outsider);
        Assert.Equal(2, outsider.State.Jobs.Active!.Stage);
        Assert.False(outsider.State.Jobs.Active.IsParticipant);
        owner.Actor.Position = job.Stages[^1].Position;
        game.Ticks(5);
        Assert.Equal(2, owner.State.Jobs.Active!.Stage);
        game.PressInteract(owner);
        Assert.Null(owner.State.Jobs.Active);
        Assert.Equal(job.Reward, owner.State.Jobs.Credits);
    }

    [Fact]
    public void DrivingJobUsesOrderedVehicleCheckpointsAndRequiresAStopForDelivery()
    {
        var game = new Harness();
        var driver = game.Join("Driver");
        var passenger = game.Join("Observer");
        var job = game.Catalog.Jobs.Single(j => j.Id == "night_dispatch");
        driver.Actor.Position = job.StartPosition;
        game.PressInteract(driver);
        Assert.Null(driver.State.Jobs.Active);

        game.EnterVehicle(driver);
        game.MoveVehicle(job.StartPosition);
        game.PressInteract(driver);
        Assert.True(driver.State.IsDriving);
        Assert.Equal(1, driver.State.Jobs.Active!.Stage);
        Assert.False(passenger.State.Jobs.Active!.IsParticipant);

        game.MoveVehicle(job.Stages[^1].Position);
        Assert.Equal(1, driver.State.Jobs.Active!.Stage);
        foreach (var stage in job.Stages.SkipLast(1)) game.MoveVehicle(stage.Position);
        Assert.Equal(job.Stages.Length, driver.State.Jobs.Active!.Stage);

        game.MoveVehicle(job.Stages[^1].Position, speed: 8);
        Assert.NotNull(driver.State.Jobs.Active);
        game.MoveVehicle(job.Stages[^1].Position);
        Assert.Null(driver.State.Jobs.Active);
        Assert.Equal(HarborJobStatus.Succeeded, driver.State.Jobs.LastResult!.Status);
        Assert.Equal(job.Reward, driver.State.Jobs.Credits);
        Assert.Equal(0, passenger.State.Jobs.Credits);
    }

    [Fact]
    public void DriveJobTimeoutReturnsToFreeRoamWithoutPayingAndCanBeRetried()
    {
        var game = new Harness();
        var driver = game.Join("Driver");
        var job = game.Catalog.Jobs.Single(j => j.Id == "basin_circuit");
        game.EnterVehicle(driver);
        game.MoveVehicle(job.StartPosition);
        game.PressInteract(driver);
        var run = driver.State.Jobs.Active!.RunId;
        game.Ticks((int)(job.TimeLimit * driver.Client.TickRate) + 3);
        Assert.Null(driver.State.Jobs.Active);
        Assert.Equal(HarborJobStatus.Failed, driver.State.Jobs.LastResult!.Status);
        Assert.Equal(0, driver.State.Jobs.LastResult.Reward);
        Assert.Equal(0, driver.State.Jobs.Credits);
        Assert.Equal(LockdownPhase.FreeRoam, driver.State.Lockdown.Phase);
        game.PressInteract(driver);
        Assert.True(driver.State.Jobs.Active!.RunId > run);
    }

    [Fact]
    public void TwoPilotsShareOneWorldWithIndependentWeaponsAndHeat()
    {
        var game = new Harness();
        var first = game.Join("Rook");
        var second = game.Join("Pilot");
        Assert.Equal(LockdownPhase.FreeRoam, first.State.Lockdown.Phase);
        Assert.Empty(first.State.Enemies);
        Assert.True(first.Client.TryGetRemote(second.Client.LocalActorId, out _));
        Assert.True(second.Client.TryGetRemote(first.Client.LocalActorId, out _));

        var magazine = first.State.PlayerCombat.Magazine;
        first.Buttons = InputButtons.Fire;
        first.Pitch = 1;
        game.Ticks(1);
        first.Buttons = InputButtons.None;
        game.Ticks(5);

        Assert.Equal(magazine - 1, first.State.PlayerCombat.Magazine);
        Assert.Equal(magazine, second.State.PlayerCombat.Magazine);
        Assert.Equal(1, first.State.WantedLevel);
        Assert.Equal(0, second.State.WantedLevel);
        Assert.Single(first.Shots);
        Assert.Single(second.Shots);
        Assert.Equal(first.Client.LocalActorId, second.Shots[0].ShooterId);

        game.Ticks(125);
        Assert.Contains(first.State.Enemies, e => e.Team == 3);
        Assert.Contains(second.State.Enemies, e => e.Team == 3);

        first.Actor.Position = new Vec3d(-180, 0, -155);
        game.Ticks(300);
        Assert.Equal(0, first.State.WantedLevel);
        Assert.DoesNotContain(first.State.Enemies, e => e.Team == 3);
    }

    [Fact]
    public void PlayerFireUsesAuthoritativeHitboxesAndReplicatesDamageToBothPilots()
    {
        var game = new Harness();
        var shooter = game.Join("Rook");
        var observer = game.Join("Pilot");
        shooter.Actor.Position = game.Authority.Lockdown.StartPosition;
        shooter.Buttons = InputButtons.Interact;
        game.Ticks(3);
        shooter.Buttons = InputButtons.None;

        var enemy = game.Authority.World.Actors.Get(game.Authority.Lockdown.EnemyIds[0]);
        shooter.Actor.Position = enemy.Position + new Vec3d(0, 0, 6);
        observer.Actor.Position = shooter.Actor.Position + new Vec3d(3, 0, 2);
        game.Ticks(8);
        var magazine = shooter.State.PlayerCombat.Magazine;

        shooter.Pitch = System.Math.Atan2(1.15 - game.Authority.Combat.Config.EyeHeight, 6);
        shooter.Buttons = InputButtons.Aim | InputButtons.Fire;
        game.Ticks(22);
        shooter.Buttons = InputButtons.None;
        game.Ticks(2);

        Assert.False(enemy.IsAlive);
        Assert.Equal(1, observer.State.Lockdown.RemainingEnemies);
        Assert.Contains(shooter.Damage, damage => damage.VictimId == enemy.Id && damage.AttackerId == shooter.Actor.Id);
        Assert.Equal(shooter.Damage, observer.Damage);
        Assert.True(shooter.State.PlayerCombat.Magazine < magazine);
        Assert.Equal(0, shooter.State.WantedLevel);
    }

    [Fact]
    public void DriverOwnsSharedCarAndOtherPilotReceivesItsMotion()
    {
        var game = new Harness();
        var first = game.Join("Rook");
        var second = game.Join("Pilot");
        var spawn = game.Authority.Vehicle.Position;
        first.Actor.Position = spawn + new Vec3d(1.7, 0, 0);
        second.Actor.Position = spawn + new Vec3d(-1.7, 0, 0);

        first.Buttons = second.Buttons = InputButtons.Interact;
        game.Ticks(3);
        Assert.Equal(first.Actor.Id, game.Authority.Lockdown.DriverId);
        Assert.Equal(first.Actor.Id, second.State.DriverId);
        Assert.True(first.State.IsDriving);
        Assert.False(second.State.IsDriving);

        first.Buttons = second.Buttons = InputButtons.None;
        first.Forward = 1;
        game.Ticks(38);

        var car = game.Authority.Vehicle;
        Assert.True(car.Speed > 3);
        Assert.True(Vec3d.Distance(spawn, car.Position) > 2.5);
        Assert.Equal(car.Position, first.Actor.Position);
        Assert.InRange(Vec3d.Distance(car.Position, second.State.Vehicle.Position), 0, 0.5);

        first.Buttons = InputButtons.Sprint;
        first.Forward = 0;
        game.Ticks(16);
        first.Buttons = InputButtons.Interact;
        game.Ticks(3);
        Assert.Equal(-1, game.Authority.Lockdown.DriverId);
        Assert.False(first.State.IsDriving);
        Assert.True(Vec3d.Distance(first.Actor.Position, car.Position) > 1.4);

        first.Buttons = InputButtons.None;
        second.Buttons = InputButtons.None;
        second.Actor.Position = car.Position + new Vec3d(1.7, 0, 0);
        game.Ticks(1);
        second.Buttons = InputButtons.Interact;
        game.Ticks(3);
        Assert.Equal(second.Actor.Id, game.Authority.Lockdown.DriverId);
        second.Endpoint.Disconnect(0);
        game.Ticks(3);
        Assert.Equal(-1, game.Authority.Lockdown.DriverId);
    }

    [Fact]
    public void OptionalEncounterCompletesBothWavesAndAuthorityRestartRestoresFreeRoam()
    {
        var game = new Harness();
        var pilot = game.Join("Rook");
        Assert.Empty(game.Authority.Lockdown.EnemyIds);
        pilot.Buttons = InputButtons.Interact;
        game.Ticks(3);
        Assert.Equal(LockdownPhase.FreeRoam, pilot.State.Lockdown.Phase);
        pilot.Buttons = InputButtons.None;
        game.Ticks(1);
        pilot.Actor.Position = game.Authority.Lockdown.StartPosition;
        pilot.Buttons = InputButtons.Interact;
        game.Ticks(3);
        Assert.Equal(LockdownPhase.FirstWave, pilot.State.Lockdown.Phase);
        Assert.Equal(2, pilot.State.Lockdown.RemainingEnemies);
        pilot.Buttons = InputButtons.None;

        game.ClearWave(pilot.Actor);
        Assert.Equal(LockdownPhase.Reinforcements, pilot.State.Lockdown.Phase);
        Assert.Equal(2, pilot.State.Lockdown.WaveNumber);
        Assert.True(pilot.State.Lockdown.ReinforcementSeconds > 0);
        game.Ticks(105);
        Assert.Equal(3, pilot.State.Lockdown.RemainingEnemies);
        game.ClearWave(pilot.Actor);
        Assert.Equal(LockdownPhase.Extraction, pilot.State.Lockdown.Phase);

        pilot.Actor.Position = pilot.State.Lockdown.ExtractionPosition;
        game.Ticks(3);
        Assert.Equal(LockdownPhase.Succeeded, pilot.State.Lockdown.Phase);
        Assert.Equal(pilot.State.Lockdown.Elapsed, pilot.State.BestTime);

        var generation = pilot.State.RunGeneration;
        pilot.Client.SendRequest(HarborProtocol.Restart, Array.Empty<byte>());
        game.Ticks(3);
        Assert.Equal(generation + 1, pilot.State.RunGeneration);
        Assert.Equal(LockdownPhase.FreeRoam, pilot.State.Lockdown.Phase);
        Assert.Empty(pilot.State.Enemies);
        Assert.Equal(game.Authority.Session.Config.SpawnPoint, pilot.Actor.Position);
        Assert.NotNull(pilot.State.BestTime);
    }

    [Fact]
    public void OnePilotCanRejoinAnActiveSharedWorldWithoutResettingIt()
    {
        var game = new Harness();
        var first = game.Join("Rook");
        var second = game.Join("Pilot");
        second.Actor.Position = game.Authority.Lockdown.StartPosition;
        second.Buttons = InputButtons.Interact;
        game.Ticks(3);
        Assert.Equal(LockdownPhase.FirstWave, first.State.Lockdown.Phase);

        second.Actor.Position = new Vec3d(-180, 0, -155);
        second.Buttons = InputButtons.None;
        game.Authority.Combat.Kill(first.Actor, -1, "test", false);
        var respawnTicks = (int)System.Math.Ceiling(game.Authority.Combat.Config.RespawnSeconds * first.Client.TickRate);
        game.Ticks(respawnTicks + 3);
        Assert.False(first.Actor.IsAlive);
        Assert.Equal(LockdownPhase.Failed, first.State.Lockdown.Phase);
        Assert.Equal(LockdownPhase.FirstWave, second.State.Lockdown.Phase);
        first.Client.SendRequest(HarborProtocol.Restart, Array.Empty<byte>());
        game.Ticks(3);
        Assert.Equal(LockdownPhase.FirstWave, first.State.Lockdown.Phase);
        Assert.Equal(second.State.RunGeneration, first.State.RunGeneration);
        Assert.True(first.Actor.IsAlive);
        Assert.Equal(first.Actor.Vitals.MaxHealth, first.Actor.Vitals.Health);
        Assert.Equal(2, first.State.Lockdown.RemainingEnemies);
    }

    [Fact]
    public void DefaultAuthorityAcceptsOnlyOnePlayer()
    {
        var game = new Harness(singlePlayer: true);
        var pilot = game.Join("Rook");
        var second = game.TryJoin("Second player");
        Assert.True(pilot.Client.IsJoined);
        Assert.False(second.Client.IsJoined);
        Assert.Equal(1, game.Authority.Session.Config.MaxPlayers);
        Assert.Single(game.Authority.World.Actors.All, actor => actor.Kind == ActorKind.Player);
    }

    [Fact]
    public void SandboxIgnoresMissionTriggersAndRespawnsAfterDefeat()
    {
        var game = new Harness(singlePlayer: true, sandbox: true);
        var pilot = game.Join("Rook");
        pilot.Actor.Position = game.Authority.Lockdown.StartPosition;
        game.PressInteract(pilot);
        Assert.Equal(LockdownPhase.FreeRoam, pilot.State.Lockdown.Phase);
        Assert.False(pilot.State.Lockdown.CanStart);
        Assert.Empty(pilot.State.Jobs.Available);
        Assert.Empty(pilot.State.Enemies);

        game.Authority.Combat.Kill(pilot.Actor, 0, "test", false);
        game.Ticks((int)System.Math.Ceiling(game.Authority.Combat.Config.RespawnSeconds * 30) + 4);
        Assert.True(pilot.Actor.IsAlive);
        Assert.Equal(game.Authority.Session.Config.SpawnPoint, pilot.Actor.Position);
        Assert.Equal(LockdownPhase.FreeRoam, pilot.State.Lockdown.Phase);
        Assert.Equal(0, pilot.State.WantedLevel);
    }

    [Fact]
    public void StoppedTrafficCanBeTakenOverAndRemainsTheSameVehicleAfterExit()
    {
        var streets = new StreetPopulationDef
        {
            TrafficRoutes =
            [
                new StreetRouteDef
                {
                    Id = "test-ring", Count = 1, Loop = true, Speed = 8,
                    VehicleModels = ["pier_runner"],
                    Points = [[-60, 0, -60], [60, 0, -60], [60, 0, 60], [-60, 0, 60]],
                }
            ],
        };
        var game = new Harness(singlePlayer: true, sandbox: true, streetOverride: streets);
        var pilot = game.Join("Rook");
        var car = Assert.Single(pilot.State.Vehicles, car => car.Occupied);
        var forward = Vec3d.FromYawPitch(car.Yaw, 0);
        pilot.Actor.Position = car.Position + forward * 10;
        game.Ticks(170);

        var stopped = Assert.Single(pilot.State.Vehicles, candidate => candidate.Id == car.Id);
        Assert.InRange(stopped.Speed, 0, 0.2);
        var right = new Vec3d(-forward.Z, 0, forward.X);
        pilot.Actor.Position = stopped.Position + right * 2;
        game.Ticks(3);
        Assert.Equal(stopped.Id, pilot.State.StreetAction.VehicleId);
        Assert.True(pilot.State.StreetAction.VehicleOccupied);

        game.Press(pilot, InputButtons.Vehicle);
        Assert.True(pilot.State.IsDriving);
        Assert.Equal(stopped.Id, pilot.State.PlayerVehicleId);
        Assert.Equal("sports", pilot.State.Vehicle.VehicleClass);
        Assert.Equal("vehicle_taken", pilot.State.StreetCrime.Status);
        Assert.Equal(1, pilot.State.WantedLevel);
        Assert.Empty(pilot.State.Ambient.Traffic);
        Assert.Single(pilot.State.Ambient.Pedestrians, person => person.Fleeing);

        game.Press(pilot, InputButtons.Vehicle);
        Assert.False(pilot.State.IsDriving);
        var left = Assert.Single(pilot.State.Vehicles, candidate => candidate.Id == car.Id);
        Assert.False(left.Occupied);
        Assert.InRange(Vec3d.Distance(left.Position, pilot.Actor.Position), 1.6, 3);
    }

    [Fact]
    public void PickpocketPaysOncePerCivilianAndStartsAVisibleReaction()
    {
        var streets = new StreetPopulationDef
        {
            PedestrianRoutes = [new StreetRouteDef { Id = "sidewalk", Count = 1, Speed = 1.25,
                Points = [[-5, 0, -8], [5, 0, -8]] }],
            PickpocketMinCash = 25,
            PickpocketMaxCash = 25,
        };
        var game = new Harness(singlePlayer: true, sandbox: true, streetOverride: streets);
        var pilot = game.Join("Rook");
        var pedestrian = Assert.Single(pilot.State.Ambient.Pedestrians);
        pilot.Actor.Position = pedestrian.Position + new Vec3d(0, 0, -1);
        game.Ticks(2);
        Assert.True(pilot.State.StreetAction.CanPickpocket);

        pilot.Buttons = InputButtons.Pickpocket;
        game.Ticks(20);
        Assert.Equal("pickpocketed", pilot.State.StreetCrime.Status);
        Assert.Equal(25, pilot.State.StreetCrime.DirtyCash);
        Assert.Equal("fleeing", Assert.Single(pilot.State.Ambient.Pedestrians).Reaction);
        Assert.False(pilot.State.StreetAction.CanPickpocket);

        pilot.Buttons = InputButtons.None;
        game.Ticks(3);
        game.Press(pilot, InputButtons.Pickpocket);
        Assert.Equal("no_target", pilot.State.StreetCrime.Status);
        Assert.Equal(25, pilot.State.StreetCrime.DirtyCash);
    }

    [Fact]
    public void CargoTheftRequiresReachAndSpawnsPoliceWhoScaleWithWantedLevel()
    {
        var streets = new StreetPopulationDef { Cargo = [new StreetCargoDef("case", [2, 0, 0], 38, 20)] };
        var game = new Harness(singlePlayer: true, sandbox: true, streetOverride: streets);
        var pilot = game.Join("Rook");
        pilot.Actor.Position = Vec3d.Zero;
        var wall = game.Authority.Level.Add(Aabb.FromCenter(new Vec3d(1, 1.1, 0), new Vec3d(0.2, 2, 4)),
            SurfaceMaterial.Concrete, "test obstacle");
        game.Press(pilot, InputButtons.Pickpocket);
        Assert.Equal("no_target", pilot.State.StreetCrime.Status);
        Assert.Equal(0, pilot.State.StreetCrime.DirtyCash);
        Assert.True(Assert.Single(pilot.State.Ambient.Cargo).Available);

        game.Authority.Level.Remove(wall);
        game.Press(pilot, InputButtons.Pickpocket);
        Assert.Equal("cargo_taken", pilot.State.StreetCrime.Status);
        Assert.Equal(38, pilot.State.StreetCrime.DirtyCash);
        Assert.False(Assert.Single(pilot.State.Ambient.Cargo).Available);
        Assert.Equal(1, pilot.State.WantedLevel);

        game.Ticks(122);
        Assert.Equal(1, pilot.State.PoliceUnits);
        var officer = Assert.Single(pilot.State.Enemies, enemy => enemy.Team == 3);
        game.Authority.World.Events.Publish(new ActorDamaged(officer.Id, pilot.Actor.Id, 1, 0, HitZone.Torso, "test"));
        game.Ticks(94);
        Assert.Equal(2, pilot.State.WantedLevel);
        Assert.Equal(2, pilot.State.PoliceUnits);
        Assert.Equal(5, pilot.State.MaxWantedLevel);
    }

    [Fact]
    public void StreetServiceChargesOnlyWhenNeededAndInRange()
    {
        var files = new DiskFileSystem(Repo.Root);
        var data = GameData.LoadCombat(files);
        var layout = new HarborLayout { Spawn = [0, 0, 0], Landmarks =
            [new HarborLandmark { Id = "customs_exchange_cafe", Position = [0, 0, 0] }] };
        var world = new SimWorld(new NullLog());
        var level = layout.CreateStaticWorld();
        var combat = world.AddSystem(new CombatSystem(data.Combat, data.Materials, data.Weapons, level, null));
        var ledger = new Harbor.Core.Economy.Ledger();
        var wallet = Harbor.Core.Economy.AccountId.Wallet("test");
        var currency = Harbor.Core.Economy.Currency.Dirty;
        ledger.Post(new Harbor.Core.Economy.Transfer("seed", Harbor.Core.Economy.AccountId.Faucet("test"), wallet, 40, currency, "test"));
        var actor = world.Actors.Spawn(ActorKind.Player, Vec3d.Zero);
        combat.Register(actor);
        var services = new HarborStreetServices(layout, combat, level, ledger);
        Assert.Equal("health", services.Describe(actor).Id);
        Assert.Equal("service_full", services.Use(actor, wallet, "full", 0, out _));
        actor.Vitals.Health = 20;
        Assert.Equal("service_wanted", services.Use(actor, wallet, "wanted", 2, out _));
        actor.Position = new Vec3d(5, 0, 0);
        Assert.Equal("service_unavailable", services.Use(actor, wallet, "far", 0, out _));
        actor.Position = Vec3d.Zero;
        Assert.Equal("service_health", services.Use(actor, wallet, "heal", 0, out var cost));
        Assert.Equal(20, cost);
        Assert.Equal(80, actor.Vitals.Health);
        Assert.Equal(20, ledger.Balance(wallet, currency));
        Assert.Equal("service_funds", services.Use(actor, wallet, "heal", 0, out _));
        Assert.Equal(80, actor.Vitals.Health);
        Assert.Equal(20, ledger.Balance(wallet, currency));
    }

    private sealed class Harness
    {
        private const double Step = 1.0 / 30.0;
        private readonly InMemoryNetwork _network = new() { LatencySeconds = 0 };
        private readonly InMemoryNetwork.Endpoint _server;
        private readonly List<Pilot> _pilots = new();
        public HarborAuthority Authority { get; }
        public HarborJobCatalog Catalog { get; }

        public Harness(bool includeAmbient = false, bool singlePlayer = false, bool sandbox = false, StreetPopulationDef? streetOverride = null)
        {
            var files = new DiskFileSystem(Repo.Root);
            var data = GameData.LoadCombat(files);
            var encounter = HarborLockdownDef.Load(files, "game/data/missions/harbor_lockdown.json", data.Weapons);
            Catalog = HarborJobCatalog.Load(files, "game/data/missions/harbor_jobs.json");
            var layout = streetOverride != null ? new HarborLayout { Spawn = [0, 0, 0] }
                : HarborLayout.Load(files, "data/world/harbor_front.json");
            var car = ArcadeVehicleTuning.Load(files, "data/vehicles/calloway_compact.json");
            _server = _network.CreateServer();
            var ambient = streetOverride ?? (includeAmbient ? StreetPopulationDef.Load(files, "data/world/harbor_population.json") : null);
            var fleet = sandbox ? VehicleCatalog.Load(files, "data/vehicles", car) : null;
            Authority = singlePlayer
                ? new HarborAuthority(data, encounter, layout, car, _server, new NullLog(), jobs: Catalog, ambient: ambient,
                    vehicles: fleet, sandbox: sandbox)
                : new HarborAuthority(data, encounter, layout, car, _server, new NullLog(), maxPlayers: 4, jobs: Catalog, ambient: ambient);
        }

        public void PressInteract(Pilot pilot) => Press(pilot, InputButtons.Interact);

        public void Press(Pilot pilot, InputButtons buttons)
        {
            pilot.Buttons = buttons;
            Ticks(3);
            pilot.Buttons = InputButtons.None;
            Ticks(3);
        }

        public void EnterVehicle(Pilot pilot)
        {
            pilot.Actor.Position = Authority.Vehicle.Position + new Vec3d(1.7, 0, 0);
            PressInteract(pilot);
            Assert.True(pilot.State.IsDriving);
        }

        public void MoveVehicle(Vec3d position, double speed = 0)
        {
            Authority.Vehicle.Restore(position, 0, speed, 0, 0);
            Ticks(3);
        }

        public Pilot Join(string name)
        {
            var pilot = TryJoin(name);
            Assert.True(pilot.Client.IsJoined);
            return pilot;
        }

        public Pilot TryJoin(string name)
        {
            var endpoint = _network.Connect(_server);
            var client = new ClientSession(endpoint, name, staticWorld: Authority.Level);
            var pilot = new Pilot(endpoint, client, Authority);
            _pilots.Add(pilot);
            Ticks(6);
            return pilot;
        }

        public void ClearWave(Actor pilot)
        {
            foreach (var id in Authority.Lockdown.EnemyIds)
            {
                var enemy = Authority.World.Actors.Get(id);
                if (enemy.IsAlive) Authority.Combat.Kill(enemy, pilot.Id, "test", false);
            }
            Ticks(3);
        }

        public void Ticks(int count)
        {
            for (var tick = 0; tick < count; tick++)
            {
                _network.Advance(Step);
                foreach (var pilot in _pilots)
                {
                    pilot.Client.Poll();
                    if (pilot.Client.IsJoined)
                        pilot.Client.Tick(0, pilot.Forward, 0, pilot.Pitch, pilot.Buttons);
                }
                _network.Advance(0);
                Authority.Update(Step);
                _network.Advance(0);
                foreach (var pilot in _pilots) pilot.Poll();
            }
        }
    }

    private sealed class Pilot(InMemoryNetwork.Endpoint endpoint, ClientSession client, HarborAuthority authority)
    {
        public InMemoryNetwork.Endpoint Endpoint { get; } = endpoint;
        public ClientSession Client { get; } = client;
        public HarborState State { get; private set; } = new();
        public Actor Actor => authority.World.Actors.Get(Client.LocalActorId);
        public InputButtons Buttons { get; set; }
        public double Forward { get; set; }
        public double Pitch { get; set; }
        public List<ShotFired> Shots { get; } = new();
        public List<ActorDamaged> Damage { get; } = new();

        public void Poll()
        {
            Client.Poll();
            while (Client.Events.TryDequeue(out var evt))
            {
                if (evt.Type == HarborProtocol.State) State = HarborProtocol.Decode<HarborState>(evt.Payload)!;
                if (evt.Type == HarborProtocol.Shot) Shots.Add(HarborProtocol.Decode<ShotFired>(evt.Payload)!);
                if (evt.Type == HarborProtocol.Damage) Damage.Add(HarborProtocol.Decode<ActorDamaged>(evt.Payload)!);
            }
        }
    }
}
