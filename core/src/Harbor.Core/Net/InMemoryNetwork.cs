using Harbor.Core.Ports;

namespace Harbor.Core.Net;

/// <summary>Simulated network for tests and same-process play: one-way latency, jitter and loss
/// for unreliable packets; reliable packets are never lost and stay ordered per link.
/// Time only moves when <see cref="Advance"/> is called, so tests are deterministic (seeded RNG).</summary>
public sealed class InMemoryNetwork(int seed = 1)
{
    public double LatencySeconds { get; set; } = 0.05;
    public double JitterSeconds { get; set; }
    public double LossRate { get; set; }
    public long BytesSent { get; private set; }

    private readonly Random _rng = new(seed);
    private readonly List<Flight> _flights = new();
    private double _now;
    private long _seq;

    private readonly record struct Flight(double DeliverAt, long Seq, Endpoint Target, NetEvent Evt);

    public Endpoint CreateServer() => new(this);

    /// <summary>Creates a client endpoint linked to <paramref name="server"/>. Both sides receive
    /// <see cref="NetEventKind.Connected"/> after one latency.</summary>
    public Endpoint Connect(Endpoint server)
    {
        var client = new Endpoint(this);
        var serverSidePeer = server.NextPeerId++;
        client.Links[0] = new Link(server, serverSidePeer);
        server.Links[serverSidePeer] = new Link(client, 0);
        Schedule(client.Links[0], new NetEvent(NetEventKind.Connected, serverSidePeer, null), reliable: true);
        Schedule(server.Links[serverSidePeer], new NetEvent(NetEventKind.Connected, 0, null), reliable: true);
        return client;
    }

    public void Advance(double seconds)
    {
        _now += seconds;
        _flights.Sort((a, b) => a.DeliverAt != b.DeliverAt ? a.DeliverAt.CompareTo(b.DeliverAt) : a.Seq.CompareTo(b.Seq));
        var i = 0;
        for (; i < _flights.Count && _flights[i].DeliverAt <= _now + 1e-9; i++)
            _flights[i].Target.Inbox.Enqueue(_flights[i].Evt);
        _flights.RemoveRange(0, i);
    }

    private void Schedule(Link link, NetEvent evtAtTarget, bool reliable)
    {
        var at = _now + LatencySeconds + (JitterSeconds > 0 ? _rng.NextDouble() * JitterSeconds : 0);
        if (reliable)
        {
            at = System.Math.Max(at, link.LastReliableAt); // keep order
            link.LastReliableAt = at;
        }
        _flights.Add(new Flight(at, _seq++, link.Remote, evtAtTarget));
    }

    internal sealed class Link(Endpoint remote, int peerIdAtRemote)
    {
        public Endpoint Remote { get; } = remote;
        public int PeerIdAtRemote { get; } = peerIdAtRemote;
        public double LastReliableAt { get; set; }
        public bool Closed { get; set; }
    }

    public sealed class Endpoint : ITransport
    {
        private readonly InMemoryNetwork _net;
        internal readonly Dictionary<int, Link> Links = new();
        internal readonly Queue<NetEvent> Inbox = new();
        internal int NextPeerId = 1;

        internal Endpoint(InMemoryNetwork net) => _net = net;

        public void Send(int peer, byte[] data, Delivery delivery)
        {
            if (!Links.TryGetValue(peer, out var link) || link.Closed) return;
            _net.BytesSent += data.Length;
            var reliable = delivery == Delivery.ReliableOrdered;
            if (!reliable && _net.LossRate > 0 && _net._rng.NextDouble() < _net.LossRate) return;
            _net.Schedule(link, new NetEvent(NetEventKind.Data, link.PeerIdAtRemote, (byte[])data.Clone()), reliable);
        }

        public bool TryReceive(out NetEvent evt) => Inbox.TryDequeue(out evt);

        public void Disconnect(int peer)
        {
            if (!Links.TryGetValue(peer, out var link) || link.Closed) return;
            link.Closed = true;
            if (link.Remote.Links.TryGetValue(link.PeerIdAtRemote, out var back)) back.Closed = true;
            _net.Schedule(link, new NetEvent(NetEventKind.Disconnected, link.PeerIdAtRemote, null), reliable: true);
            Inbox.Enqueue(new NetEvent(NetEventKind.Disconnected, peer, null));
        }
    }
}
