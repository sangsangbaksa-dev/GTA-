using System.Text;
using Harbor.Core.Math;

namespace Harbor.Core.Net;

/// <summary>Little-endian binary writer with varints and game-specific quantizers.
/// Quantizers live here (not in callers) so client prediction and server use identical precision.</summary>
public sealed class NetWriter(int capacity = 256)
{
    private byte[] _buf = new byte[capacity];
    public int Length { get; private set; }

    public ReadOnlySpan<byte> Span => _buf.AsSpan(0, Length);
    public byte[] ToArray() => _buf.AsSpan(0, Length).ToArray();
    public void Clear() => Length = 0;

    private Span<byte> Take(int n)
    {
        if (Length + n > _buf.Length) Array.Resize(ref _buf, System.Math.Max(_buf.Length * 2, Length + n));
        var s = _buf.AsSpan(Length, n);
        Length += n;
        return s;
    }

    public void WriteByte(byte v) => Take(1)[0] = v;
    public void WriteSByte(sbyte v) => Take(1)[0] = unchecked((byte)v);
    public void WriteBool(bool v) => WriteByte(v ? (byte)1 : (byte)0);
    public void WriteUShort(ushort v) { var s = Take(2); s[0] = (byte)v; s[1] = (byte)(v >> 8); }
    public void WriteInt(int v) => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);
    public void WriteLong(long v) => System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(Take(8), v);
    public void WriteFloat(float v) => System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(Take(4), v);
    public void WriteDouble(double v) => System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(Take(8), v);

    public void WriteVarUInt(ulong v)
    {
        while (v >= 0x80) { WriteByte((byte)(v | 0x80)); v >>= 7; }
        WriteByte((byte)v);
    }

    public void WriteVarInt(long v) => WriteVarUInt((ulong)((v << 1) ^ (v >> 63)));

    public void WriteString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteVarUInt((ulong)bytes.Length);
        bytes.CopyTo(Take(bytes.Length));
    }

    public void WriteBytes(ReadOnlySpan<byte> b) { WriteVarUInt((ulong)b.Length); b.CopyTo(Take(b.Length)); }

    /// <summary>Position quantized to 1 cm.</summary>
    public void WritePosition(Vec3d p) { WriteVarInt(Quant.Cm(p.X)); WriteVarInt(Quant.Cm(p.Y)); WriteVarInt(Quant.Cm(p.Z)); }
    public void WriteAngle(double radians) => WriteUShort(Quant.Angle(radians));
}

/// <summary>Shared quantization rules.</summary>
public static class Quant
{
    public static long Cm(double meters) => (long)System.Math.Round(meters * 100.0);
    public static double FromCm(long cm) => cm / 100.0;

    public static ushort Angle(double radians)
    {
        var turns = radians / (2 * System.Math.PI);
        turns -= System.Math.Floor(turns);
        return (ushort)((long)System.Math.Round(turns * 65536.0) & 0xFFFF);
    }

    public static double FromAngle(ushort a) => a / 65536.0 * 2 * System.Math.PI;

    /// <summary>Signed angle (pitch) in [-π, π).</summary>
    public static double FromAngleSigned(ushort a) { var r = FromAngle(a); return r >= System.Math.PI ? r - 2 * System.Math.PI : r; }

    public static sbyte Axis(double v) => (sbyte)System.Math.Round(System.Math.Clamp(v, -1.0, 1.0) * 127.0);
    public static double FromAxis(sbyte v) => v / 127.0;
}
