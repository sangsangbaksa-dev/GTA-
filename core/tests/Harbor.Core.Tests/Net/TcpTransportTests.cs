using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Ports;
using Harbor.Core.Sim;
using Xunit;

namespace Harbor.Core.Tests.Net;

public sealed class TcpTransportTests
{
    [Fact]
    public async Task TwoClientsReceiveAuthoritativeMovementOverTcp()
    {
        using var serverTransport = TcpTransport.Listen(IPAddress.Loopback, 0, maxPeers: 2);
        var port = serverTransport.LocalEndPoint.Port;
        using var transportA = await TcpTransport.ConnectAsync("127.0.0.1", port);
        using var transportB = await TcpTransport.ConnectAsync("127.0.0.1", port);
        var world = new SimWorld(new NullLog());
        var server = world.AddSystem(new ServerSession(serverTransport, new ServerConfig { MaxPlayers = 2 }));
        var playerA = new ClientSession(transportA, "Rook");
        var playerB = new ClientSession(transportB, "Pilot");

        void Pump()
        {
            serverTransport.Pump();
            world.Update(world.Loop.StepSeconds);
            transportA.Pump();
            transportB.Pump();
            playerA.Poll();
            playerB.Poll();
        }

        await Until(() =>
        {
            Pump();
            return playerA.IsJoined && playerA.LocalServerState.Id == playerA.LocalActorId
                && playerB.IsJoined && playerB.LocalServerState.Id == playerB.LocalActorId;
        });
        Assert.NotEqual(playerA.LocalActorId, playerB.LocalActorId);
        Assert.Equal(2, server.Clients.Count);
        var start = playerA.LocalServerState.Position;
        await Until(() =>
        {
            playerA.Tick(0, 1, 0, 0, InputButtons.None);
            playerB.Tick(0, 0, 0, 0, InputButtons.None);
            Pump();
            return playerA.LastAckedSequence > 10
                && Vec3d.Distance(start, playerA.LocalServerState.Position) > 0.5
                && playerB.TryGetRemote(playerA.LocalActorId, out var observed)
                && Vec3d.Distance(start, observed.Position) > 0.25;
        });
    }

    [Fact]
    public async Task ReassemblesFragmentedFramesAndRejectsOversizedPacket()
    {
        using var transport = TcpTransport.Listen(IPAddress.Loopback, 0, maxPeers: 1);
        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, transport.LocalEndPoint.Port);
        NetEvent next = default;
        await Until(() =>
        {
            transport.Pump();
            return transport.TryReceive(out next);
        });
        Assert.Equal(NetEventKind.Connected, next.Kind);
        var peer = next.Peer;

        await raw.GetStream().WriteAsync(new byte[] { 0, 0 });
        transport.Pump();
        Assert.False(transport.TryReceive(out _));

        await raw.GetStream().WriteAsync(new byte[] { 0, 3, 10 });
        transport.Pump();
        Assert.False(transport.TryReceive(out _));
        await raw.GetStream().WriteAsync(new byte[] { 20, 30 });
        await Until(() =>
        {
            transport.Pump();
            return transport.TryReceive(out next);
        });
        Assert.Equal(NetEventKind.Data, next.Kind);
        Assert.Equal(peer, next.Peer);
        Assert.Equal(new byte[] { 10, 20, 30 }, next.Data);

        var badFrame = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(badFrame, TcpTransport.MaxPacketBytes + 1);
        await raw.GetStream().WriteAsync(badFrame);
        await Until(() =>
        {
            transport.Pump();
            return transport.TryReceive(out next);
        });
        Assert.Equal(NetEventKind.Disconnected, next.Kind);
        Assert.Equal(peer, next.Peer);
    }

    [Fact]
    public async Task ClosesConnectionsBeyondCapacityAndReusesFreeSlot()
    {
        using var server = TcpTransport.Listen(IPAddress.Loopback, 0, maxPeers: 1);
        var port = server.LocalEndPoint.Port;
        using var first = await TcpTransport.ConnectAsync("127.0.0.1", port);
        server.Pump();
        Assert.True(server.TryReceive(out var joined));
        Assert.Equal(NetEventKind.Connected, joined.Kind);
        using var extra = await TcpTransport.ConnectAsync("127.0.0.1", port);
        await Until(() =>
        {
            server.Pump();
            extra.Pump();
            return !extra.IsConnected;
        });
        Assert.False(server.TryReceive(out _));

        first.Dispose();
        NetEvent left = default;
        await Until(() =>
        {
            server.Pump();
            return server.TryReceive(out left);
        });
        Assert.Equal(new NetEvent(NetEventKind.Disconnected, joined.Peer, null), left);

        using var replacement = await TcpTransport.ConnectAsync("127.0.0.1", port);
        NetEvent rejoined = default;
        await Until(() =>
        {
            server.Pump();
            return server.TryReceive(out rejoined);
        });
        Assert.Equal(joined, rejoined);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(2, deadline.Token);
    }
}
