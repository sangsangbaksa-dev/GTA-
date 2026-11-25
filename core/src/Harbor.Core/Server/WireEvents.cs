using Harbor.Core.Combat;
using Harbor.Core.Math;
using Harbor.Core.Net;

namespace Harbor.Core.Server;

/// <summary>Reliable server → client game events (type ids are part of the protocol; never reuse a number).</summary>
public static class WireEventType
{
    public const ushort Damaged = 1;
    public const ushort Killed = 2;
    public const ushort Downed = 3;
    public const ushort Revived = 4;
    public const ushort Respawned = 5;
    public const ushort Explosion = 6;
    public const ushort Shot = 7;
    public const ushort Ammo = 8;
    public const ushort Balance = 20;
    public const ushort Purchase = 21;
    public const ushort CashDropped = 22;
    public const ushort ActivityUpdate = 30;
    public const ushort ActivityResult = 31;
    public const ushort HeistSplit = 32;
    public const ushort Notice = 40;
    public const ushort RequestRejected = 41;
    public const ushort Wanted = 42;
    /// <summary>Presentation info for an actor (model, display name, team) — sent on spawn/join.</summary>
    public const ushort ActorInfo = 50;
    public const ushort ActorRemoved = 51;
    public const ushort Travelled = 52;
}

/// <summary>What a client needs to draw an actor that snapshots don't carry.</summary>
public readonly record struct ActorInfoMsg(int Id, Sim.ActorKind Kind, string Model, string Name, int Team);

/// <summary>Encoders shared by server and client. Clients decode with <see cref="NetReader"/> in the same order.</summary>
public static class WireEvents
{
    public static byte[] Damaged(ActorDamaged e)
    {
        var w = new NetWriter(16);
        w.WriteVarUInt((uint)e.VictimId); w.WriteVarInt(e.AttackerId); w.WriteVarUInt((uint)e.HealthLost);
        w.WriteVarUInt((uint)e.ArmorLost); w.WriteByte((byte)e.Zone); w.WriteString(e.Source);
        return w.ToArray();
    }

    public static ActorDamaged ReadDamaged(byte[] p)
    {
        var r = new NetReader(p);
        return new ActorDamaged((int)r.ReadVarUInt32(), (int)r.ReadVarInt(), (int)r.ReadVarUInt32(), (int)r.ReadVarUInt32(), (HitZone)r.ReadByte(), r.ReadString());
    }

    public static byte[] Killed(ActorKilled e)
    {
        var w = new NetWriter(16);
        w.WriteVarUInt((uint)e.VictimId); w.WriteVarInt(e.KillerId); w.WriteString(e.Source); w.WriteBool(e.Headshot);
        return w.ToArray();
    }

    public static ActorKilled ReadKilled(byte[] p)
    {
        var r = new NetReader(p);
        return new ActorKilled((int)r.ReadVarUInt32(), (int)r.ReadVarInt(), r.ReadString(), r.ReadBool());
    }

    public static byte[] ActorPair(int a, int b)
    {
        var w = new NetWriter(8);
        w.WriteVarUInt((uint)a); w.WriteVarInt(b);
        return w.ToArray();
    }

    public static (int A, int B) ReadActorPair(byte[] p)
    {
        var r = new NetReader(p);
        return ((int)r.ReadVarUInt32(), (int)r.ReadVarInt());
    }

    public static byte[] Point(int id, Vec3d pos)
    {
        var w = new NetWriter(16);
        w.WriteVarInt(id); w.WritePosition(pos);
        return w.ToArray();
    }

    public static (int Id, Vec3d Pos) ReadPoint(byte[] p)
    {
        var r = new NetReader(p);
        return ((int)r.ReadVarInt(), r.ReadPosition());
    }

    public static byte[] Shot(ShotFired e)
    {
        var w = new NetWriter(24);
        w.WriteVarUInt((uint)e.ShooterId); w.WriteString(e.WeaponId); w.WritePosition(e.Origin); w.WritePosition(e.End);
        return w.ToArray();
    }

    public static ShotFired ReadShot(byte[] p)
    {
        var r = new NetReader(p);
        return new ShotFired((int)r.ReadVarUInt32(), r.ReadString(), r.ReadPosition(), r.ReadPosition(), 0);
    }

    public static byte[] ActorInfo(ActorInfoMsg m)
    {
        var w = new NetWriter(32);
        w.WriteVarUInt((uint)m.Id); w.WriteByte((byte)m.Kind); w.WriteString(m.Model); w.WriteString(m.Name); w.WriteVarInt(m.Team);
        return w.ToArray();
    }

    public static ActorInfoMsg ReadActorInfo(byte[] p)
    {
        var r = new NetReader(p);
        return new ActorInfoMsg((int)r.ReadVarUInt32(), (Sim.ActorKind)r.ReadByte(), r.ReadString(), r.ReadString(), (int)r.ReadVarInt());
    }

    public static byte[] Text(string s)
    {
        var w = new NetWriter(s.Length + 4);
        w.WriteString(s);
        return w.ToArray();
    }

    public static string ReadText(byte[] p) { var r = new NetReader(p); return r.ReadString(1024); }

    public static byte[] Ammo(string weaponId, int inMagazine, int reserve)
    {
        var w = new NetWriter(16);
        w.WriteString(weaponId); w.WriteVarUInt((uint)inMagazine); w.WriteVarUInt((uint)reserve);
        return w.ToArray();
    }

    public static (string WeaponId, int InMagazine, int Reserve) ReadAmmo(byte[] p)
    {
        var r = new NetReader(p);
        return (r.ReadString(), (int)r.ReadVarUInt32(), (int)r.ReadVarUInt32());
    }

    // ------------------------------------------------------------------ activities

    /// <summary>Objective started (progress 0) or progressed. Progress is sent in 1/1000 steps.</summary>
    public static byte[] Objective(int runId, int index, string label, Vec3d target, double progress)
    {
        var w = new NetWriter(32);
        w.WriteVarUInt((uint)runId); w.WriteVarUInt((uint)index); w.WriteString(label); w.WritePosition(target);
        w.WriteUShort((ushort)System.Math.Clamp(System.Math.Round(progress * 1000), 0, 1000));
        return w.ToArray();
    }

    public static (int RunId, int Index, string Label, Vec3d Target, double Progress) ReadObjective(byte[] p)
    {
        var r = new NetReader(p);
        return ((int)r.ReadVarUInt32(), (int)r.ReadVarUInt32(), r.ReadString(), r.ReadPosition(), r.ReadUShort() / 1000.0);
    }

    public static byte[] Result(int runId, string contractId, bool success, string reason, long paidTotal)
    {
        var w = new NetWriter(32);
        w.WriteVarUInt((uint)runId); w.WriteString(contractId); w.WriteBool(success); w.WriteString(reason); w.WriteVarInt(paidTotal);
        return w.ToArray();
    }

    public static (int RunId, string ContractId, bool Success, string Reason, long Paid) ReadResult(byte[] p)
    {
        var r = new NetReader(p);
        return ((int)r.ReadVarUInt32(), r.ReadString(), r.ReadBool(), r.ReadString(), r.ReadVarInt());
    }

    public static byte[] Split(int runId, string heistId, long take, double secondsLeft, IReadOnlyList<string> members)
    {
        var w = new NetWriter(64);
        w.WriteVarUInt((uint)runId); w.WriteString(heistId); w.WriteVarInt(take); w.WriteVarUInt((uint)System.Math.Ceiling(secondsLeft));
        w.WriteByte((byte)members.Count);
        foreach (var m in members) w.WriteString(m);
        return w.ToArray();
    }

    public static (int RunId, string HeistId, long Take, int SecondsLeft, List<string> Members) ReadSplit(byte[] p)
    {
        var r = new NetReader(p);
        var run = (int)r.ReadVarUInt32(); var id = r.ReadString(); var take = r.ReadVarInt(); var secs = (int)r.ReadVarUInt32();
        var n = System.Math.Min((int)r.ReadByte(), 4);
        var members = new List<string>();
        for (var i = 0; i < n; i++) members.Add(r.ReadString());
        return (run, id, take, secs, members);
    }

    /// <summary>World notice for everyone: supply drops, turf attacks, bounties. <c>kind</c> e.g. "supply.announced".</summary>
    public static byte[] Notice(string kind, string id, Vec3d position, long value)
    {
        var w = new NetWriter(48);
        w.WriteString(kind); w.WriteString(id); w.WritePosition(position); w.WriteVarInt(value);
        return w.ToArray();
    }

    public static (string Kind, string Id, Vec3d Position, long Value) ReadNotice(byte[] p)
    {
        var r = new NetReader(p);
        return (r.ReadString(), r.ReadString(), r.ReadPosition(), r.ReadVarInt());
    }
}
