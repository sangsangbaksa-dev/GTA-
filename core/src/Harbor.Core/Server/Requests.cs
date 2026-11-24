using Harbor.Core.Economy;
using Harbor.Core.Net;

namespace Harbor.Core.Server;

/// <summary>Client → server reliable requests. Every request carries a client nonce; the server turns it
/// into a ledger TxId so a resent request never charges twice.</summary>
public static class RequestType
{
    public const ushort Buy = 1;
    public const ushort LaunderInstant = 2;
    public const ushort LaunderAtBusiness = 3;
    public const ushort BuyBusiness = 4;
    public const ushort StashDeposit = 5;
    public const ushort StashWithdraw = 6;
    public const ushort CrewCreate = 7;
    public const ushort CrewJoin = 8;
    public const ushort CrewDeposit = 9;
    public const ushort CrewWithdraw = 10;
    public const ushort TogglePassive = 11;
    /// <summary>A = contract id.</summary>
    public const ushort ContractStart = 20;
    /// <summary>Y = heist run id, X = shares packed one byte per member (<see cref="Activities.ContractBoard.PackPercents"/>).</summary>
    public const ushort HeistSplit = 21;
    public const ushort ContractAbandon = 22;
    public const ushort CarryDrop = 23;
}

/// <summary>Generic request body: nonce + up to three strings + up to two integers (keeps the codec tiny and safe).</summary>
public sealed record RequestBody(ulong Nonce, string A = "", string B = "", long X = 0, long Y = 0)
{
    public byte[] Encode()
    {
        var w = new NetWriter(32);
        w.WriteVarUInt(Nonce); w.WriteString(A); w.WriteString(B); w.WriteVarInt(X); w.WriteVarInt(Y);
        return w.ToArray();
    }

    public static RequestBody Decode(byte[] payload)
    {
        var r = new NetReader(payload);
        return new RequestBody(r.ReadVarUInt(), r.ReadString(64), r.ReadString(64), r.ReadVarInt(), r.ReadVarInt());
    }
}

public static class RequestCodec
{
    public static Currency ParseCurrency(long v) => v == 1 ? Currency.Clean : Currency.Dirty;

    public static byte[] Balance(long walletDirty, long bankClean, long stashDirty)
    {
        var w = new NetWriter(16);
        w.WriteVarInt(walletDirty); w.WriteVarInt(bankClean); w.WriteVarInt(stashDirty);
        return w.ToArray();
    }

    public static (long Wallet, long Bank, long Stash) ReadBalance(byte[] p)
    {
        var r = new NetReader(p);
        return (r.ReadVarInt(), r.ReadVarInt(), r.ReadVarInt());
    }

    public static byte[] Rejected(ushort requestType, ulong nonce, string reason)
    {
        var w = new NetWriter(24);
        w.WriteUShort(requestType); w.WriteVarUInt(nonce); w.WriteString(reason);
        return w.ToArray();
    }

    public static (ushort Type, ulong Nonce, string Reason) ReadRejected(byte[] p)
    {
        var r = new NetReader(p);
        return (r.ReadUShort(), r.ReadVarUInt(), r.ReadString());
    }
}
