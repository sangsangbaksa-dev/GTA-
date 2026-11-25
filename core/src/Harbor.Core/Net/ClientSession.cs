using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Ports;
using Harbor.Core.Sim;

namespace Harbor.Core.Net;

/// <summary>Client half of the netcode (engine-agnostic so it is testable; Godot drives it).
/// Each fixed tick: sample input → predict own movement → send the last few inputs.
/// On snapshot: adopt the server state for our actor and replay unacknowledged inputs
/// (reconciliation); buffer remote actors for interpolation.</summary>
public sealed class ClientSession(ITransport transport, string playerName,
    MovementConfig? movement = null, StaticWorld? staticWorld = null, double interpolationDelayTicks = 3)
{
    public MovementConfig MovementConfig { get; } = movement ?? new MovementConfig();
    public bool IsJoined => LocalActorId >= 0;
    public int LocalActorId { get; private set; } = -1;
    public double TickRate { get; private set; } = 30;
    public uint LatestServerTick { get; private set; }
    public uint LastAckedSequence { get; private set; }
    /// <summary>Predicted state of the local actor (what the player sees).</summary>
    public MoveState Predicted => _predicted;
    /// <summary>Server-confirmed vitals/flags of the local actor.</summary>
    public EntityState LocalServerState { get; private set; }
    /// <summary>Distance the prediction was corrected by on the last snapshot (diagnostics/smoothing).</summary>
    public double LastCorrection { get; private set; }
    public double RenderTick { get; private set; }
    public int PendingInputs => _pending.Count;
    public SnapshotInterpolator Remote { get; } = new();
    public Queue<(ushort Type, byte[] Payload)> Events { get; } = new();

    private readonly List<InputCmd> _pending = new();
    private MoveState _predicted;
    private ActorFlags _status;
    private uint _sequence;

    public void SendRequest(ushort type, ReadOnlySpan<byte> payload)
        => transport.Send(0, Messages.Request(type, payload), Delivery.ReliableOrdered);

    public void Poll()
    {
        while (transport.TryReceive(out var evt))
        {
            switch (evt.Kind)
            {
                case NetEventKind.Connected:
                    transport.Send(0, Messages.Hello(playerName), Delivery.ReliableOrdered);
                    break;
                case NetEventKind.Disconnected:
                    LocalActorId = -1;
                    break;
                case NetEventKind.Data when evt.Data != null:
                    Handle(evt.Data);
                    break;
            }
        }
    }

    /// <summary>Call once per fixed client tick (same rate as the server).</summary>
    public InputCmd Tick(double moveRight, double moveForward, double yaw, double pitch, InputButtons buttons, byte weaponSlot = 0)
    {
        RenderTick += 1;
        var cmd = InputCmd.Create(++_sequence, (uint)System.Math.Max(0, System.Math.Floor(RenderTick)),
            moveRight, moveForward, yaw, pitch, buttons, weaponSlot);
        if (!IsJoined) return cmd;
        _pending.Add(cmd);
        if (_pending.Count > 120) _pending.RemoveAt(0); // server unreachable; don't grow forever
        Movement.Step(ref _predicted, cmd, 1.0 / TickRate, MovementConfig, Movement.ModifiersFor(_status, MovementConfig), staticWorld);
        transport.Send(0, Messages.Inputs(_pending.TakeLast(Messages.MaxInputsPerPacket).ToList()), Delivery.Unreliable);
        return cmd;
    }

    public bool TryGetRemote(int actorId, out EntityState state) => Remote.TrySample(actorId, RenderTick, out state);

    private void Handle(byte[] data)
    {
        var r = new NetReader(data);
        switch ((MsgType)r.ReadByte())
        {
            case MsgType.Welcome:
                LocalActorId = (int)r.ReadVarUInt32();
                LatestServerTick = r.ReadVarUInt32();
                TickRate = r.ReadFloat();
                RenderTick = LatestServerTick - interpolationDelayTicks;
                break;
            case MsgType.Snapshot:
                OnSnapshot(Messages.ReadSnapshot(ref r));
                break;
            case MsgType.GameEvent:
                Events.Enqueue(Messages.ReadTagged(ref r));
                break;
        }
    }

    private void OnSnapshot(Snapshot snap)
    {
        if (snap.ServerTick < LatestServerTick) return; // stale (reordered packet)
        LatestServerTick = snap.ServerTick;
        Remote.Add(snap.ServerTick, snap.Entities, skipId: LocalActorId);
        Remote.Prune((double)snap.ServerTick - 90);

        // keep render time ~delay behind the newest snapshot, nudging gently to avoid visible jumps
        var target = (double)snap.ServerTick - interpolationDelayTicks;
        RenderTick = System.Math.Abs(RenderTick - target) > 15 ? target : RenderTick + (target - RenderTick) * 0.1;

        var self = snap.Entities.FirstOrDefault(e => e.Id == LocalActorId);
        if (self.Id != LocalActorId || LocalActorId < 0) return;
        LocalServerState = self;
        _status = self.Flags & (ActorFlags.Downed | ActorFlags.Dead | ActorFlags.Carrying | ActorFlags.Passive);
        if (snap.AckSequence < LastAckedSequence) return;
        LastAckedSequence = snap.AckSequence;

        var before = _predicted.Position;
        _predicted.Position = self.Position;
        _predicted.Velocity = self.Velocity;
        _pending.RemoveAll(c => c.Sequence <= snap.AckSequence);
        var dt = 1.0 / TickRate;
        var mods = Movement.ModifiersFor(_status, MovementConfig);
        foreach (var cmd in _pending) Movement.Step(ref _predicted, cmd, dt, MovementConfig, mods, staticWorld);
        LastCorrection = Vec3d.Distance(before, _predicted.Position);
    }
}
