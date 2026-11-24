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
    public const ushort Notice = 40;
    public const ushort RequestRejected = 41;
}

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
}
