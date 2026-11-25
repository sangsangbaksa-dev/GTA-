using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Harbor.Core.Ports;

namespace Harbor.Core.Net;

/// <summary>
/// Length-prefixed TCP transport for the dedicated server and desktop client. Call Pump once
/// per update. Call all instance methods serially.
/// </summary>
public sealed class TcpTransport : ITransport, IDisposable
{
    public const int MaxPacketBytes = 32 * 1024;
    private const int HeaderBytes = sizeof(int);
    private const int IncomingPerPeerPerPump = 8;
    private const int MaxIoCallsPerPump = 32;
    private const int MaxOutgoingPerPeer = 64;
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(5);

    private readonly Socket? _listener;
    private readonly int _maxPeers;
    private readonly int _inboxLimit;
    private readonly Dictionary<int, Peer> _peers = new();
    private readonly HashSet<int> _closingPeerIds = new();
    private readonly Queue<NetEvent> _inbox = new();
    private bool _disposed;

    public IPEndPoint LocalEndPoint { get; }
    public bool IsConnected => _listener == null && _peers.ContainsKey(0);

    private TcpTransport(Socket socket, int maxPeers, bool listening)
    {
        _maxPeers = maxPeers;
        _inboxLimit = maxPeers * (IncomingPerPeerPerPump + 2);
        LocalEndPoint = (IPEndPoint)socket.LocalEndPoint!;
        if (listening)
            _listener = socket;
        else
            AddPeer(0, socket);
    }

    public static TcpTransport Listen(IPAddress address, int port, int maxPeers = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPeers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPeers, 64);
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(address, port));
            socket.Listen(maxPeers);
            socket.Blocking = false;
            return new TcpTransport(socket, maxPeers, listening: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static async Task<TcpTransport> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ConnectLimit);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
            return new TcpTransport(socket, 1, listening: false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Pump()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = Stopwatch.GetTimestamp();
        foreach (var (id, peer) in _peers.ToArray())
        {
            if (Stopwatch.GetElapsedTime(peer.LastFrameAt, now) >= IdleLimit ||
                !peer.Flush() || !Read(id, peer, now))
                Disconnect(id);
        }

        if (_listener == null) return;
        for (var attempt = 0; attempt < _maxPeers && _listener.Poll(0, SelectMode.SelectRead); attempt++)
        {
            Socket socket;
            try { socket = _listener.Accept(); }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.WouldBlock) { break; }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionAborted) { continue; }

            var id = 1;
            while (_peers.ContainsKey(id) || _closingPeerIds.Contains(id)) id++;
            // Leave room for a disconnected event for every live peer.
            if (id > _maxPeers || _inbox.Count + _peers.Count + 2 > _inboxLimit)
            {
                socket.Dispose();
                continue;
            }

            try { AddPeer(id, socket); }
            catch (SocketException) { socket.Dispose(); }
        }
    }

    public void Send(int peer, byte[] data, Delivery delivery)
    {
        if (!_peers.TryGetValue(peer, out var connection)) return;
        if (data.Length is 0 or > MaxPacketBytes)
            throw new ArgumentOutOfRangeException(nameof(data), "TCP packet length exceeds the transport limits.");

        if (connection.Outgoing.Count >= MaxOutgoingPerPeer)
        {
            if (delivery == Delivery.ReliableOrdered) Disconnect(peer);
            return;
        }

        var frame = new byte[HeaderBytes + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, data.Length);
        data.CopyTo(frame, HeaderBytes);
        connection.Outgoing.Enqueue(frame);
        if (!connection.Flush()) Disconnect(peer);
    }

    public bool TryReceive(out NetEvent evt)
    {
        if (!_inbox.TryDequeue(out evt)) return false;
        if (evt.Kind == NetEventKind.Disconnected) _closingPeerIds.Remove(evt.Peer);
        return true;
    }

    public void Disconnect(int peer)
    {
        if (!_peers.Remove(peer, out var connection)) return;
        connection.Socket.Dispose();
        _closingPeerIds.Add(peer);
        _inbox.Enqueue(new NetEvent(NetEventKind.Disconnected, peer, null));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener?.Dispose();
        foreach (var peer in _peers.Values) peer.Socket.Dispose();
        _peers.Clear();
        _closingPeerIds.Clear();
        _inbox.Clear();
    }

    private void AddPeer(int id, Socket socket)
    {
        socket.NoDelay = true;
        socket.Blocking = false;
        _peers.Add(id, new Peer(socket));
        _inbox.Enqueue(new NetEvent(NetEventKind.Connected, id, null));
    }

    private bool Read(int id, Peer peer, long now)
    {
        var delivered = 0;
        for (var call = 0; call < MaxIoCallsPerPump && delivered < IncomingPerPeerPerPump; call++)
        {
            if (_inbox.Count + _peers.Count >= _inboxLimit) break;
            var expected = peer.PacketLength == 0 ? HeaderBytes : peer.PacketLength;
            var target = peer.Incoming.AsSpan(peer.BytesRead, expected - peer.BytesRead);
            var count = peer.Socket.Receive(target, SocketFlags.None, out var error);
            if (error == SocketError.WouldBlock) break;
            if (error != SocketError.Success || count == 0) return false;

            peer.BytesRead += count;
            if (peer.BytesRead < expected) continue;
            peer.BytesRead = 0;
            if (peer.PacketLength == 0)
            {
                peer.PacketLength = BinaryPrimitives.ReadInt32BigEndian(peer.Incoming);
                if (peer.PacketLength is <= 0 or > MaxPacketBytes) return false;
                continue;
            }

            var packet = peer.Incoming.AsSpan(0, peer.PacketLength).ToArray();
            _inbox.Enqueue(new NetEvent(NetEventKind.Data, id, packet));
            peer.LastFrameAt = now;
            peer.PacketLength = 0;
            delivered++;
        }

        return true;
    }

    private sealed class Peer(Socket socket)
    {
        public Socket Socket { get; } = socket;
        public byte[] Incoming { get; } = new byte[MaxPacketBytes];
        public Queue<byte[]> Outgoing { get; } = new();
        public int BytesRead { get; set; }
        public int PacketLength { get; set; }
        public long LastFrameAt { get; set; } = Stopwatch.GetTimestamp();
        private int _bytesWritten;

        public bool Flush()
        {
            for (var call = 0; call < MaxIoCallsPerPump && Outgoing.TryPeek(out var frame); call++)
            {
                var count = Socket.Send(frame.AsSpan(_bytesWritten), SocketFlags.None, out var error);
                if (error == SocketError.WouldBlock) break;
                if (error != SocketError.Success || count == 0) return false;
                _bytesWritten += count;
                if (_bytesWritten < frame.Length) continue;
                Outgoing.Dequeue();
                _bytesWritten = 0;
            }

            return true;
        }
    }
}
