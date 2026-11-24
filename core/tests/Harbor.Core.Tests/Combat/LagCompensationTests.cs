using Harbor.Core.Combat;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Server;
using Xunit;

namespace Harbor.Core.Tests.Combat;

/// <summary>End-to-end: real netcode + server combat. The shooter aims at what its client renders.</summary>
public class LagCompensationTests
{
    private static (int hits, int shots, List<(ushort, byte[])> shooterEvents) Duel(double maxRewind, double latency = 0.1)
    {
        var net = new InMemoryNetwork(11) { LatencySeconds = latency, JitterSeconds = 0.01 };
        var serverEp = net.CreateServer();
        var server = new GameServer(Repo.Data, serverEp, new NullLog(), config: new ServerConfig { MaxRewindSeconds = maxRewind });
        var shooter = new ClientSession(net.Connect(serverEp), "shooter");
        var target = new ClientSession(net.Connect(serverEp), "target");
        var hits = 0;
        server.World.Events.Subscribe<ActorDamaged>(e => { if (e.VictimId == target.LocalActorId) hits++; });
        var shots = 0;
        var dt = server.World.Loop.StepSeconds;
        var events = new List<(ushort, byte[])>();

        for (var t = 0; t < 200; t++)
        {
            net.Advance(dt);
            shooter.Poll();
            target.Poll();
            while (shooter.Events.TryDequeue(out var e)) events.Add(e);

            if (t == 30)
            {
                // face each other 12 m apart (spawn points are side by side)
                server.World.Actors.Get(shooter.LocalActorId).Position = Vec3d.Zero;
                server.World.Actors.Get(target.LocalActorId).Position = new Vec3d(0, 0, -12);
            }
            // target strafes left/right across the shooter's view
            var strafe = (t / 40) % 2 == 0 ? 1.0 : -1.0;
            target.Tick(strafe, 0, 0, 0, InputButtons.None);

            double yaw = 0, pitch = 0;
            var fire = false;
            if (shooter.IsJoined && target.IsJoined && shooter.TryGetRemote(target.LocalActorId, out var seen) && t > 60)
            {
                var eye = shooter.Predicted.Position + new Vec3d(0, Repo.Data.Combat.EyeHeight, 0);
                var d = (seen.Position + new Vec3d(0, 1.15, 0) - eye).Normalized();
                yaw = System.Math.Atan2(-d.X, -d.Z);
                pitch = System.Math.Asin(d.Y);
                fire = t % 12 == 0 && shots < 4; // semi-auto pistol: press, then release
                if (fire) shots++;
            }
            shooter.Tick(0, 0, yaw, pitch, fire ? InputButtons.Fire | InputButtons.Aim : InputButtons.Aim);
            server.Update(dt);
        }
        return (hits, shots, events);
    }

    [Fact]
    public void Rewind_Lets_Shooter_Hit_What_They_See()
    {
        var (hits, shots, _) = Duel(maxRewind: 0.35);
        Assert.Equal(4, shots);
        Assert.True(hits >= 3, $"hits {hits}/{shots}");
    }

    [Fact]
    public void Without_Rewind_Moving_Targets_Are_Missed()
    {
        var (hits, shots, _) = Duel(maxRewind: 0.0);
        Assert.Equal(4, shots);
        Assert.True(hits <= 1, $"hits {hits}/{shots}");
    }

    [Fact]
    public void Shooter_Receives_Hit_Marker_And_Ammo_Events()
    {
        var (_, _, events) = Duel(0.35);
        Assert.Contains(events, e => e.Item1 == WireEventType.Damaged);
        Assert.Contains(events, e => e.Item1 == WireEventType.Ammo);
    }
}
