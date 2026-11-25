using Harbor.Core.Net;
using Harbor.Core.Server;
using Xunit;

namespace Harbor.Core.Tests.Economy;

/// <summary>End-to-end: client requests over the wire, server-side ledger, balance/rejection events back.</summary>
public class ServerEconomyTests
{
    private sealed class Rig
    {
        public readonly InMemoryNetwork Net = new(5) { LatencySeconds = 0.05, LossRate = 0.1 };
        public readonly GameServer Server;
        public readonly ClientSession Client;
        public readonly List<(ushort Type, byte[] Payload)> Events = new();

        public Rig()
        {
            var ep = Net.CreateServer();
            Server = new GameServer(Repo.Data, ep, new NullLog());
            Client = new ClientSession(Net.Connect(ep), "buyer");
            Run(30);
        }

        public void Run(int ticks)
        {
            var dt = Server.World.Loop.StepSeconds;
            for (var i = 0; i < ticks; i++)
            {
                Net.Advance(dt);
                Client.Poll();
                while (Client.Events.TryDequeue(out var e)) Events.Add(e);
                Client.Tick(0, 0, 0, 0, InputButtons.None);
                Server.Update(dt);
            }
        }

        public (long Wallet, long Bank, long Stash) LastBalance() =>
            RequestCodec.ReadBalance(Events.Last(e => e.Type == WireEventType.Balance).Payload);
    }

    [Fact]
    public void Join_Sends_Starting_Balance()
    {
        var rig = new Rig();
        Assert.True(rig.Client.IsJoined);
        Assert.Equal(Repo.Data.Economy.Config.StartingBalance.Clean, rig.LastBalance().Bank);
    }

    [Fact]
    public void Resent_Buy_Request_Charges_Once()
    {
        var rig = new Rig();
        var before = rig.LastBalance().Bank;
        var req = new RequestBody(Nonce: 42, A: "ammo_pistol", X: 1).Encode();
        rig.Client.SendRequest(RequestType.Buy, req);
        rig.Client.SendRequest(RequestType.Buy, req); // duplicate (client retry)
        rig.Run(30);
        Assert.Equal(before - 40, rig.LastBalance().Bank);
        Assert.Contains(rig.Events, e => e.Type == WireEventType.Purchase);
    }

    [Fact]
    public void Unaffordable_Purchase_Is_Rejected_With_Reason()
    {
        var rig = new Rig();
        rig.Client.SendRequest(RequestType.Buy, new RequestBody(7, A: "ammo_pistol", X: 0).Encode()); // dirty wallet is empty
        rig.Run(30);
        var rej = rig.Events.Where(e => e.Type == WireEventType.RequestRejected)
            .Select(e => RequestCodec.ReadRejected(e.Payload)).Single();
        Assert.Equal(RequestType.Buy, rej.Type);
        Assert.Equal(7ul, rej.Nonce);
        Assert.False(string.IsNullOrEmpty(rej.Reason));
    }

    [Fact]
    public void Malformed_Request_Does_Not_Crash_Server()
    {
        var rig = new Rig();
        rig.Client.SendRequest(RequestType.Buy, new byte[] { 0xFF, 0xFF, 0xFF });
        rig.Client.SendRequest(999, new RequestBody(1).Encode());
        rig.Run(20);
        Assert.True(rig.Client.IsJoined);
    }
}
