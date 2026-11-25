using Harbor.Core.Net;
using Harbor.Core.Ports;
using Xunit;

namespace Harbor.Core.Tests.Net;

public class TransportTests
{
    private static List<NetEvent> Drain(ITransport t)
    {
        var l = new List<NetEvent>();
        while (t.TryReceive(out var e)) l.Add(e);
        return l;
    }

    [Fact]
    public void Delivery_Waits_For_Latency()
    {
        var net = new InMemoryNetwork { LatencySeconds = 0.1 };
        var server = net.CreateServer();
        var client = net.Connect(server);
        net.Advance(0.05);
        Assert.Empty(Drain(server));
        net.Advance(0.06);
        Assert.Equal(NetEventKind.Connected, Assert.Single(Drain(server)).Kind);
        Assert.Equal(NetEventKind.Connected, Assert.Single(Drain(client)).Kind);
    }

    [Fact]
    public void Reliable_Survives_Loss_In_Order_Unreliable_Drops()
    {
        var net = new InMemoryNetwork(3) { LatencySeconds = 0.03, JitterSeconds = 0.05, LossRate = 0.5 };
        var server = net.CreateServer();
        var client = net.Connect(server);
        net.Advance(1); Drain(server);
        for (byte i = 0; i < 100; i++)
        {
            client.Send(0, new[] { i }, Delivery.ReliableOrdered);
            client.Send(0, new[] { (byte)(200) }, Delivery.Unreliable);
        }
        net.Advance(1);
        var data = Drain(server).Where(e => e.Kind == NetEventKind.Data).Select(e => e.Data![0]).ToList();
        var reliable = data.Where(b => b < 200).ToList();
        Assert.Equal(Enumerable.Range(0, 100).Select(i => (byte)i), reliable);
        var unreliable = data.Count(b => b == 200);
        Assert.InRange(unreliable, 30, 70);
    }

    [Fact]
    public void Disconnect_Notifies_Both_Sides()
    {
        var net = new InMemoryNetwork();
        var server = net.CreateServer();
        var client = net.Connect(server);
        net.Advance(1); Drain(server); Drain(client);
        client.Disconnect(0);
        net.Advance(1);
        Assert.Contains(Drain(server), e => e.Kind == NetEventKind.Disconnected);
    }
}
