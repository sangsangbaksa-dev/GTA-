using Harbor.Core.Combat;
using Harbor.Core.Data;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Ports;
using Harbor.Core.Server;
using Harbor.Core.Sim;
using Harbor.Core.Vehicles;
using Xunit;

namespace Harbor.Core.Tests.Server;

public sealed class HarborAuthorityTests
{
    [Fact]
    public void TwoPilotsShareOneWorldWithIndependentWeaponsAndHeat()
    {
        var game = new Harness();
        var first = game.Join("Rook");
        var second = game.Join("Pilot");
        Assert.Equal(LockdownPhase.FreeRoam, first.State.Lockdown.Phase);
        Assert.Empty(first.State.Enemies);
        Assert.Equal(second.Client.LocalActorId, Assert.Single(first.State.RemotePlayers).Id);
        Assert.Equal(first.Client.LocalActorId, Assert.Single(second.State.RemotePlayers).Id);

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

    private sealed class Harness
    {
        private const double Step = 1.0 / 30.0;
        private readonly InMemoryNetwork _network = new() { LatencySeconds = 0 };
        private readonly InMemoryNetwork.Endpoint _server;
        private readonly List<Pilot> _pilots = new();
        public HarborAuthority Authority { get; }

        public Harness()
        {
            var files = new DiskFileSystem(Repo.Root);
            var data = GameData.LoadCombat(files);
            var encounter = HarborLockdownDef.Load(files, "game/data/missions/harbor_lockdown.json", data.Weapons);
            var layout = HarborLayout.Load(files, "data/world/harbor_front.json");
            var car = ArcadeVehicleTuning.Load(files, "data/vehicles/calloway_compact.json");
            _server = _network.CreateServer();
            Authority = new HarborAuthority(data, encounter, layout, car, _server, new NullLog());
        }

        public Pilot Join(string name)
        {
            var endpoint = _network.Connect(_server);
            var client = new ClientSession(endpoint, name, staticWorld: Authority.Level);
            var pilot = new Pilot(endpoint, client, Authority);
            _pilots.Add(pilot);
            Ticks(6);
            Assert.True(client.IsJoined);
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
