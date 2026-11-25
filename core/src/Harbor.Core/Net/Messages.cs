using Harbor.Core.Math;
using Harbor.Core.Sim;

namespace Harbor.Core.Net;

public enum MsgType : byte
{
    Hello = 1,      // client → server, reliable
    Welcome = 2,    // server → client, reliable
    Input = 3,      // client → server, unreliable (last N inputs, redundancy against loss)
    Snapshot = 4,   // server → client, unreliable
    GameEvent = 5,  // server → client, reliable (economy, kills, activity updates)
    Request = 6,    // client → server, reliable (buy, launder, start contract ...)
}

/// <summary>Replicated view of one actor inside a snapshot.</summary>
public readonly record struct EntityState(
    int Id, ActorKind Kind, Vec3d Position, Vec3d Velocity, double Yaw, double Pitch,
    ActorFlags Flags, byte Health, byte Armor)
{
    public static EntityState From(Actor a) => new(a.Id, a.Kind, a.Move.Position, a.Move.Velocity, a.Move.Yaw, a.Move.Pitch,
        a.Flags, (byte)System.Math.Clamp(a.Vitals.Health, 0, 255), (byte)System.Math.Clamp(a.Vitals.Armor, 0, 255));

    public void Write(NetWriter w)
    {
        w.WriteVarUInt((uint)Id);
        w.WriteByte((byte)Kind);
        w.WritePosition(Position);
        w.WritePosition(Velocity);
        w.WriteAngle(Yaw);
        w.WriteAngle(Pitch);
        w.WriteByte((byte)Flags);
        w.WriteByte(Health);
        w.WriteByte(Armor);
    }

    public static EntityState Read(ref NetReader r) => new(
        (int)r.ReadVarUInt32(), (ActorKind)r.ReadByte(), r.ReadPosition(), r.ReadPosition(),
        r.ReadAngle(), Quant.FromAngleSigned(r.ReadUShort()), (ActorFlags)r.ReadByte(), r.ReadByte(), r.ReadByte());
}

public sealed record Snapshot(uint ServerTick, uint AckSequence, IReadOnlyList<EntityState> Entities);

/// <summary>Encoders/decoders for every message. One place so client and server cannot drift.</summary>
public static class Messages
{
    public const ushort ProtocolVersion = 2;
    public const int MaxInputsPerPacket = 4;
    public const int MaxEntitiesPerSnapshot = 256;
    public const int MaxGameEventPayload = 24 * 1024;

    public static byte[] Hello(string playerName)
    {
        var w = new NetWriter(32);
        w.WriteByte((byte)MsgType.Hello);
        w.WriteUShort(ProtocolVersion);
        w.WriteString(playerName);
        return w.ToArray();
    }

    public static byte[] Welcome(int actorId, uint serverTick, double tickRate)
    {
        var w = new NetWriter(16);
        w.WriteByte((byte)MsgType.Welcome);
        w.WriteVarUInt((uint)actorId);
        w.WriteVarUInt(serverTick);
        w.WriteFloat((float)tickRate);
        return w.ToArray();
    }

    public static byte[] Inputs(IReadOnlyList<InputCmd> newestLast)
    {
        var w = new NetWriter(64);
        w.WriteByte((byte)MsgType.Input);
        var n = System.Math.Min(newestLast.Count, MaxInputsPerPacket);
        w.WriteByte((byte)n);
        for (var i = newestLast.Count - n; i < newestLast.Count; i++) newestLast[i].Write(w);
        return w.ToArray();
    }

    public static byte[] SnapshotPacket(uint serverTick, uint ack, IReadOnlyList<EntityState> entities)
    {
        var w = new NetWriter(64 + entities.Count * 24);
        w.WriteByte((byte)MsgType.Snapshot);
        w.WriteVarUInt(serverTick);
        w.WriteVarUInt(ack);
        w.WriteVarUInt((uint)entities.Count);
        foreach (var e in entities) e.Write(w);
        return w.ToArray();
    }

    public static byte[] GameEvent(ushort eventType, ReadOnlySpan<byte> payload) => Tagged(MsgType.GameEvent, eventType, payload);
    public static byte[] Request(ushort requestType, ReadOnlySpan<byte> payload) => Tagged(MsgType.Request, requestType, payload);

    private static byte[] Tagged(MsgType t, ushort type, ReadOnlySpan<byte> payload)
    {
        var w = new NetWriter(8 + payload.Length);
        w.WriteByte((byte)t);
        w.WriteUShort(type);
        w.WriteBytes(payload);
        return w.ToArray();
    }

    public static (ushort type, byte[] payload) ReadTagged(ref NetReader r) => (r.ReadUShort(), r.ReadBytes());

    public static (ushort type, byte[] payload) ReadGameEvent(ref NetReader r) =>
        (r.ReadUShort(), r.ReadBytes(MaxGameEventPayload));

    public static List<InputCmd> ReadInputs(ref NetReader r)
    {
        var n = r.ReadByte();
        if (n > MaxInputsPerPacket) throw new NetFormatException("too many inputs");
        var list = new List<InputCmd>(n);
        for (var i = 0; i < n; i++) list.Add(InputCmd.Read(ref r));
        return list;
    }

    public static Snapshot ReadSnapshot(ref NetReader r)
    {
        var tick = r.ReadVarUInt32();
        var ack = r.ReadVarUInt32();
        var n = r.ReadCount(MaxEntitiesPerSnapshot);
        var list = new List<EntityState>(n);
        for (var i = 0; i < n; i++) list.Add(EntityState.Read(ref r));
        return new Snapshot(tick, ack, list);
    }
}
