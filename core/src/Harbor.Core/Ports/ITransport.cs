namespace Harbor.Core.Ports;

public enum NetEventKind { Connected, Disconnected, Data }

/// <summary>Transport event. For a client transport the server is always peer 0.</summary>
public readonly record struct NetEvent(NetEventKind Kind, int Peer, byte[]? Data);

public enum Delivery
{
    /// <summary>May be dropped or reordered (inputs, snapshots).</summary>
    Unreliable,
    /// <summary>Delivered once, in order per peer (handshake, economy/game events).</summary>
    ReliableOrdered,
}

/// <summary>Network transport port. Implementations: InMemoryNetwork (tests, local play),
/// TcpTransport (LAN desktop and dedicated server).</summary>
public interface ITransport
{
    void Send(int peer, byte[] data, Delivery delivery);
    bool TryReceive(out NetEvent evt);
    void Disconnect(int peer);
}
