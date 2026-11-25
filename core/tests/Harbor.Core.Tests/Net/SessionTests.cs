using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Sim;
using Xunit;

namespace Harbor.Core.Tests.Net;

public class SessionTests
{
    private static (double, double, double, InputButtons) Forward(int i, ClientSession c) => (0, 1, 0, InputButtons.None);

    [Fact]
    public void Clients_Join_And_Get_Actors()
    {
        var h = new NetHarness();
        var a = h.AddClient("alpha");
        var b = h.AddClient("bravo");
        h.JoinAll();
        Assert.True(a.IsJoined && b.IsJoined);
        Assert.NotEqual(a.LocalActorId, b.LocalActorId);
        Assert.Equal(2, h.World.Actors.Count);
        Assert.Equal("bravo", h.World.Actors.Get(b.LocalActorId).Name);
    }

    [Theory]
    [InlineData(0.05, 0.0)]
    [InlineData(0.10, 0.10)]
    [InlineData(0.15, 0.25)]
    public void Prediction_Matches_Server_Under_Latency_And_Loss(double latency, double loss)
    {
        var h = new NetHarness(latency, loss, jitter: 0.02);
        var c = h.AddClient("p");
        h.JoinAll();
        h.Run(20); // settle on spawn position
        h.Run(90, Forward);
        h.Run(30); // stop and let the last inputs drain
        var server = h.World.Actors.Get(c.LocalActorId).Position;
        Assert.True(server.Z < -8, $"server actor should have moved, z={server.Z}");
        Assert.True(Vec3d.Distance(server, c.Predicted.Position) < 0.05,
            $"prediction {c.Predicted.Position} vs server {server}");
        Assert.True(c.LastCorrection < 0.05);
    }

    [Fact]
    public void Server_Correction_Overrides_Prediction()
    {
        var h = new NetHarness(0.05);
        var c = h.AddClient("p");
        h.JoinAll();
        h.Run(10);
        // server teleports the player (e.g. respawn); client must converge
        h.World.Actors.Get(c.LocalActorId).Position = new Vec3d(50, 0, 50);
        h.Run(15);
        Assert.True(Vec3d.Distance(c.Predicted.Position, new Vec3d(50, 0, 50)) < 0.01);
    }

    [Fact]
    public void Cheating_Client_Cannot_Move_Faster_Than_Rules()
    {
        var h = new NetHarness();
        var c = h.AddClient("p");
        h.JoinAll();
        var start = h.World.Actors.Get(c.LocalActorId).Position;
        // hostile client sends out-of-range axis values; quantization clamps to ±1
        h.Run(30, (i, cl) => (0, 50, 0, InputButtons.Sprint));
        var moved = Vec3d.Distance(start, h.World.Actors.Get(c.LocalActorId).Position);
        Assert.True(moved <= h.Server.MovementConfig.SprintSpeed * 1.0 + 0.01, $"moved {moved}");
    }

    [Fact]
    public void Remote_Players_Interpolate_Smoothly()
    {
        var h = new NetHarness(0.06, jitter: 0.02);
        var mover = h.AddClient("mover");
        var watcher = h.AddClient("watcher");
        h.JoinAll();
        h.Run(20);
        var samples = new List<double>();
        for (var t = 0; t < 60; t++)
        {
            h.Step((i, cl) => i == 0 ? (0, 1, 0, InputButtons.None) : (0, 0, 0, InputButtons.None));
            if (watcher.TryGetRemote(mover.LocalActorId, out var s)) samples.Add(s.Position.Z);
        }
        Assert.True(samples.Count > 40);
        // moving along -Z: interpolated Z must never jump backwards
        for (var i = 1; i < samples.Count; i++) Assert.True(samples[i] <= samples[i - 1] + 1e-6, $"jitter at {i}");
        Assert.True(samples[^1] < samples[0] - 3);
    }

    [Fact]
    public void Interest_Management_Hides_Far_Actors()
    {
        var h = new NetHarness(config: new ServerConfig { InterestRadius = 100 });
        var c = h.AddClient("p");
        h.JoinAll();
        var far = h.World.Actors.Spawn(ActorKind.Npc, new Vec3d(1000, 0, 0));
        var near = h.World.Actors.Spawn(ActorKind.Npc, new Vec3d(20, 0, 0));
        h.Run(15);
        Assert.Contains(near.Id, c.Remote.Ids);
        Assert.DoesNotContain(far.Id, c.Remote.Ids);
    }

    [Fact]
    public void Disconnect_Removes_Actor()
    {
        var h = new NetHarness();
        var c = h.AddClient("p");
        h.JoinAll();
        var left = new List<PlayerLeft>();
        h.World.Events.Subscribe<PlayerLeft>(left.Add);
        h.ServerEndpoint.Disconnect(h.Server.Clients.Keys.Single());
        h.Run(5);
        Assert.Single(left);
        Assert.Equal(0, h.World.Actors.Count);
    }

    [Fact]
    public void LagComp_Rewinds_To_Claimed_View_And_Clamps()
    {
        var h = new NetHarness(0.1);
        var c = h.AddClient("p");
        h.JoinAll();
        h.Run(10);
        h.Run(30, Forward);
        var now = h.Server.Tick;
        Assert.True(h.Server.History.TryGet(now - 5, c.LocalActorId, out var past));
        Assert.True(h.Server.History.TryGet(now, c.LocalActorId, out var current));
        Assert.True(past.Position.Z > current.Position.Z); // was behind
        Assert.Equal(now - h.Server.MaxRewindTicks, h.Server.History.ClampTick(0, now, h.Server.MaxRewindTicks));
    }

    [Fact]
    public void Inputs_Report_View_Tick_For_Lag_Compensation()
    {
        var h = new NetHarness(0.1);
        var c = h.AddClient("p");
        h.JoinAll();
        var applied = new List<PlayerInputApplied>();
        h.World.Events.Subscribe<PlayerInputApplied>(applied.Add);
        h.Run(40, (i, cl) => (0, 0, 0, InputButtons.Fire));
        var last = applied[^1];
        // ~100 ms up + 100 ms interpolation delay + snapshot spacing → clamped to max rewind
        Assert.InRange(last.ServerTick - last.ViewTick, 3, h.Server.MaxRewindTicks);
    }

    [Fact]
    public void Bad_Packets_Are_Dropped_Not_Fatal()
    {
        var h = new NetHarness();
        var c = h.AddClient("p");
        h.JoinAll();
        var raw = h.Network.Connect(h.ServerEndpoint);
        h.Run(3);
        raw.Send(0, new byte[] { (byte)MsgType.Hello, 1 }, Harbor.Core.Ports.Delivery.ReliableOrdered);
        h.Run(3);
        Assert.True(c.IsJoined);
        Assert.Contains(((NullLog)h.World.Log).Warnings, w => w.Contains("bad packet"));
    }
}
