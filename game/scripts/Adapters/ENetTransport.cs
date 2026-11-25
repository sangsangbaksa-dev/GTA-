using System.Collections.Generic;
using Godot;
using Harbor.Core.Ports;

namespace Harbor.Game.Adapters;

/// <summary><see cref="ITransport"/> over Godot's ENet (UDP). Channel 0 = reliable ordered, channel 1 = unreliable.
/// Used for the listen server, the headless dedicated server and clients (ADR 0004/0008).
/// For a client transport the server is peer 0, matching the port contract.</summary>
public sealed class ENetTransport : ITransport
{
    private const int Channels = 2;
    private readonly ENetConnection _host = new();
    private readonly bool _isServer;
    private readonly Dictionary<ulong, int> _idByPeer = new();
    private readonly Dictionary<int, ENetPacketPeer> _peerById = new();
    private readonly Queue<NetEvent> _inbox = new();
    private int _nextId = 1;

    public bool IsServer => _isServer;
    public int PeerCount => _peerById.Count;

    private ENetTransport(bool isServer) => _isServer = isServer;

    public static ENetTransport Listen(int port, int maxPeers)
    {
        var t = new ENetTransport(true);
        var err = t._host.CreateHostBound("*", port, maxPeers, Channels);
        if (err != Error.Ok) throw new System.InvalidOperationException($"ENet listen on {port} failed: {err}");
        return t;
    }

    public static ENetTransport Connect(string address, int port)
    {
        var t = new ENetTransport(false);
        var err = t._host.CreateHost(1, Channels);
        if (err != Error.Ok) throw new System.InvalidOperationException($"ENet client host failed: {err}");
        var peer = t._host.ConnectToHost(address, port, Channels);
        if (peer == null) throw new System.InvalidOperationException($"ENet connect to {address}:{port} failed");
        return t;
    }

    public void Send(int peer, byte[] data, Delivery delivery)
    {
        if (!_peerById.TryGetValue(peer, out var p)) return;
        if (delivery == Delivery.ReliableOrdered) p.Send(0, data, (int)ENetPacketPeer.FlagReliable);
        else p.Send(1, data, (int)ENetPacketPeer.FlagUnsequenced);
    }

    public bool TryReceive(out NetEvent evt)
    {
        if (_inbox.Count == 0) Pump();
        return _inbox.TryDequeue(out evt);
    }

    public void Disconnect(int peer)
    {
        if (!_peerById.Remove(peer, out var p)) return;
        _idByPeer.Remove(p.GetInstanceId());
        p.PeerDisconnectLater();
        _inbox.Enqueue(new NetEvent(NetEventKind.Disconnected, peer, null));
    }

    public void Close()
    {
        foreach (var p in _peerById.Values) p.PeerDisconnectNow();
        _peerById.Clear();
        _idByPeer.Clear();
        _host.Flush();
        _host.Destroy();
    }

    private void Pump()
    {
        for (var guard = 0; guard < 4096; guard++)
        {
            var ev = _host.Service();
            var type = (ENetConnection.EventType)(int)ev[0];
            if (type == ENetConnection.EventType.None) break;
            if (type == ENetConnection.EventType.Error) { _inbox.Enqueue(new NetEvent(NetEventKind.Disconnected, 0, null)); break; }
            var peer = ev[1].As<ENetPacketPeer>();
            if (peer == null) continue;
            var key = peer.GetInstanceId();
            switch (type)
            {
                case ENetConnection.EventType.Connect:
                {
                    var id = _isServer ? _nextId++ : 0;
                    _idByPeer[key] = id;
                    _peerById[id] = peer;
                    _inbox.Enqueue(new NetEvent(NetEventKind.Connected, id, null));
                    break;
                }
                case ENetConnection.EventType.Disconnect:
                    if (_idByPeer.Remove(key, out var gone))
                    {
                        _peerById.Remove(gone);
                        _inbox.Enqueue(new NetEvent(NetEventKind.Disconnected, gone, null));
                    }
                    break;
                case ENetConnection.EventType.Receive:
                {
                    var data = peer.GetPacket();
                    if (_idByPeer.TryGetValue(key, out var from)) _inbox.Enqueue(new NetEvent(NetEventKind.Data, from, data));
                    break;
                }
            }
        }
    }
}
