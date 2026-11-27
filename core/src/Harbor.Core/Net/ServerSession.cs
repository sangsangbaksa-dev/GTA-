using Harbor.Core.Events;
using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Ports;
using Harbor.Core.Sim;

namespace Harbor.Core.Net;

public sealed record ServerConfig
{
    public int MaxPlayers { get; init; } = 16;
    public double SnapshotRateHz { get; init; } = 20;
    /// <summary>Actors farther than this from a player are not replicated to them (interest management).</summary>
    public double InterestRadius { get; init; } = 512;
    /// <summary>Covers RTT ≈ 250 ms plus the 100 ms interpolation delay; higher pings are disadvantaged on purpose.</summary>
    public double MaxRewindSeconds { get; init; } = 0.35;
    public double HistorySeconds { get; init; } = 1.0;
    public int MaxBufferedInputs { get; init; } = 32;
    public Vec3d SpawnPoint { get; init; } = Vec3d.Zero;
}

public sealed record PlayerJoined(int Peer, int ActorId, string Name) : ISimEvent;
public sealed record PlayerLeft(int Peer, int ActorId) : ISimEvent;

/// <summary>Published after the server applied one input for a player this tick. Combat, interaction
/// and activities react to it (fire, reload, interact). <see cref="ViewTick"/> is already clamped for rewind.</summary>
public sealed record PlayerInputApplied(int Peer, int ActorId, InputCmd Cmd, long ServerTick, long ViewTick) : ISimEvent;

/// <summary>A reliable request from a client (buy, launder, accept contract...). Handlers validate everything.</summary>
public sealed record ClientRequest(int Peer, int ActorId, ushort Type, byte[] Payload) : ISimEvent;

/// <summary>Authoritative server: owns player actors, consumes their inputs at the fixed tick,
/// moves them with the shared <see cref="Movement"/>, records lag-compensation history and
/// sends per-client snapshots. Runs inside <see cref="SimWorld"/> as a system, with no engine.</summary>
public sealed class ServerSession(ITransport transport, ServerConfig? config = null,
    MovementConfig? movement = null, StaticWorld? staticWorld = null) : ISimSystem
{
    public ServerConfig Config { get; } = config ?? new ServerConfig();
    public MovementConfig MovementConfig { get; } = movement ?? new MovementConfig();
    public StaticWorld? StaticWorld { get; } = staticWorld;
    public Func<Actor, bool>? ShouldApplyMovement { get; set; }
    public Action<Actor>? ResolveMovementContacts { get; set; }
    public LagCompHistory History { get; private set; } = null!;
    public long Tick { get; private set; }
    public int MaxRewindTicks { get; private set; }
    public IReadOnlyDictionary<int, Client> Clients => _clients;
    public long BytesSentSnapshots { get; private set; }

    private readonly Dictionary<int, Client> _clients = new();
    private SimWorld _world = null!;
    private double _snapAccum;
    private double _tickRate;

    public sealed class Client(int peer)
    {
        public int Peer { get; } = peer;
        public int ActorId { get; internal set; } = -1;
        public string Name { get; internal set; } = "";
        public uint LastProcessed { get; internal set; }
        public InputCmd LastInput { get; internal set; }
        public int MissedInputs { get; internal set; }
        internal readonly SortedDictionary<uint, InputCmd> Buffer = new();
        public bool Joined => ActorId >= 0;
    }

    public void Initialize(SimWorld world)
    {
        _world = world;
        _tickRate = 1.0 / world.Loop.StepSeconds;
        MaxRewindTicks = (int)System.Math.Round(Config.MaxRewindSeconds * _tickRate);
        History = new LagCompHistory((int)System.Math.Ceiling(Config.HistorySeconds * _tickRate) + 2);
    }

    public void Step(double dt)
    {
        Tick++;
        Pump();
        foreach (var c in _clients.Values.Where(c => c.Joined).ToList()) ApplyInput(c, dt);
        History.Record(Tick, _world.Actors.All);
        _snapAccum += Config.SnapshotRateHz / _tickRate;
        if (_snapAccum >= 1.0)
        {
            _snapAccum -= 1.0;
            foreach (var c in _clients.Values.Where(c => c.Joined)) SendSnapshot(c);
        }
    }

    public void SendEvent(int peer, ushort type, ReadOnlySpan<byte> payload)
        => transport.Send(peer, Messages.GameEvent(type, payload), Delivery.ReliableOrdered);

    public void Broadcast(ushort type, ReadOnlySpan<byte> payload)
    {
        var bytes = Messages.GameEvent(type, payload);
        foreach (var c in _clients.Values.Where(c => c.Joined)) transport.Send(c.Peer, bytes, Delivery.ReliableOrdered);
    }

    public bool TryGetClientByActor(int actorId, out Client client)
    {
        client = _clients.Values.FirstOrDefault(c => c.ActorId == actorId)!;
        return client != null;
    }

    private void Pump()
    {
        while (transport.TryReceive(out var evt))
        {
            switch (evt.Kind)
            {
                case NetEventKind.Connected:
                    if (_clients.Count >= Config.MaxPlayers) { transport.Disconnect(evt.Peer); break; }
                    _clients[evt.Peer] = new Client(evt.Peer);
                    break;
                case NetEventKind.Disconnected:
                    if (_clients.Remove(evt.Peer, out var gone) && gone.Joined)
                    {
                        _world.Actors.Remove(gone.ActorId);
                        _world.Events.Publish(new PlayerLeft(gone.Peer, gone.ActorId));
                    }
                    break;
                case NetEventKind.Data when evt.Data != null && _clients.TryGetValue(evt.Peer, out var client):
                    try { Handle(client, evt.Data); }
                    catch (NetFormatException e) { _world.Log.Warn($"peer {evt.Peer}: bad packet ({e.Message})"); }
                    break;
            }
        }
    }

    private void Handle(Client c, byte[] data)
    {
        var r = new NetReader(data);
        switch ((MsgType)r.ReadByte())
        {
            case MsgType.Hello when !c.Joined:
                if (r.ReadUShort() != Messages.ProtocolVersion) { transport.Disconnect(c.Peer); return; }
                c.Name = r.ReadString(32);
                var actor = _world.Actors.Spawn(ActorKind.Player, Config.SpawnPoint + new Vec3d(c.Peer * 1.5, 0, 0));
                actor.OwnerPeer = c.Peer;
                actor.Name = c.Name;
                c.ActorId = actor.Id;
                transport.Send(c.Peer, Messages.Welcome(actor.Id, (uint)Tick, _tickRate), Delivery.ReliableOrdered);
                _world.Events.Publish(new PlayerJoined(c.Peer, actor.Id, c.Name));
                break;
            case MsgType.Input when c.Joined:
                foreach (var cmd in Messages.ReadInputs(ref r))
                    if (cmd.Sequence > c.LastProcessed && c.Buffer.Count < Config.MaxBufferedInputs)
                        c.Buffer.TryAdd(cmd.Sequence, cmd);
                break;
            case MsgType.Request when c.Joined:
                var (type, payload) = Messages.ReadTagged(ref r);
                _world.Events.Publish(new ClientRequest(c.Peer, c.ActorId, type, payload));
                break;
        }
    }

    private void ApplyInput(Client c, double dt)
    {
        if (!_world.Actors.TryGet(c.ActorId, out var actor)) return;
        InputCmd cmd;
        bool fresh;
        if (c.Buffer.Count > 0)
        {
            // Take the oldest unprocessed input. Gaps (lost packets the redundancy didn't cover) are skipped.
            var first = c.Buffer.First();
            c.Buffer.Remove(first.Key);
            cmd = first.Value;
            c.LastProcessed = cmd.Sequence;
            fresh = true;
        }
        else
        {
            // Starved: repeat the last movement intent but never repeat one-shot actions.
            cmd = c.LastInput with
            {
                Buttons = c.LastInput.Buttons & ~(InputButtons.Fire | InputButtons.Reload | InputButtons.Interact
                    | InputButtons.Melee | InputButtons.Vehicle | InputButtons.Pickpocket | InputButtons.Jump)
            };
            c.MissedInputs++;
            fresh = false;
        }
        c.LastInput = cmd;
        var mods = Movement.ModifiersFor(actor.Status, MovementConfig);
        if (ShouldApplyMovement?.Invoke(actor) != false)
        {
            Movement.Step(ref actor.Move, cmd, dt, MovementConfig, mods, StaticWorld);
            ResolveMovementContacts?.Invoke(actor);
        }
        if (fresh)
        {
            var view = History.ClampTick(cmd.ViewTick, Tick, MaxRewindTicks);
            _world.Events.Publish(new PlayerInputApplied(c.Peer, actor.Id, cmd, Tick, view));
        }
    }

    private void SendSnapshot(Client c)
    {
        if (!_world.Actors.TryGet(c.ActorId, out var self)) return;
        var r2 = Config.InterestRadius * Config.InterestRadius;
        var list = new List<EntityState>();
        foreach (var a in _world.Actors.All)
            if (a.Id == self.Id || (a.Position - self.Position).LengthSquared <= r2)
                list.Add(EntityState.From(a));
        if (list.Count > Messages.MaxEntitiesPerSnapshot)
        {
            list = list.OrderBy(e => e.Id == self.Id ? -1 : (e.Position - self.Position).LengthSquared)
                       .Take(Messages.MaxEntitiesPerSnapshot).ToList();
        }
        var bytes = Messages.SnapshotPacket((uint)Tick, c.LastProcessed, list);
        BytesSentSnapshots += bytes.Length;
        transport.Send(c.Peer, bytes, Delivery.Unreliable);
    }
}
