using System.Text;
using Harbor.Core.Math;

namespace Harbor.Core.Net;

public sealed class NetFormatException(string message) : Exception(message);

/// <summary>Bounds-checked reader for <see cref="NetWriter"/> output. Malformed input throws
/// <see cref="NetFormatException"/> — the server drops the packet (never trust client bytes).</summary>
public ref struct NetReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos = 0;

    public readonly int Remaining => _data.Length - _pos;
    public readonly bool AtEnd => _pos >= _data.Length;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > _data.Length) throw new NetFormatException("read past end");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public byte ReadByte() => Take(1)[0];
    public sbyte ReadSByte() => unchecked((sbyte)Take(1)[0]);
    public bool ReadBool() => ReadByte() != 0;
    public ushort ReadUShort() { var s = Take(2); return (ushort)(s[0] | (s[1] << 8)); }
    public int ReadInt() => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public long ReadLong() => System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public float ReadFloat() => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double ReadDouble() => System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    public ulong ReadVarUInt()
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new NetFormatException("varint too long");
    }

    public long ReadVarInt() { var u = ReadVarUInt(); return (long)(u >> 1) ^ -(long)(u & 1); }

    public uint ReadVarUInt32()
    {
        var v = ReadVarUInt();
        if (v > uint.MaxValue) throw new NetFormatException("value out of range");
        return (uint)v;
    }

    public int ReadCount(int max)
    {
        var v = ReadVarUInt();
        if (v > (ulong)max) throw new NetFormatException($"count {v} > {max}");
        return (int)v;
    }

    public string ReadString(int maxBytes = 256) => Encoding.UTF8.GetString(Take(ReadCount(maxBytes)));
    public byte[] ReadBytes(int maxBytes = 4096) => Take(ReadCount(maxBytes)).ToArray();

    public Vec3d ReadPosition() => new(Quant.FromCm(ReadVarInt()), Quant.FromCm(ReadVarInt()), Quant.FromCm(ReadVarInt()));
    public double ReadAngle() => Quant.FromAngle(ReadUShort());
}
