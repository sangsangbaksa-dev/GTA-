using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Sim;
using Xunit;

namespace Harbor.Core.Tests.Net;

public class SerializationTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(300L)]
    [InlineData(-123456789L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void VarInt_RoundTrips(long v)
    {
        var w = new NetWriter();
        w.WriteVarInt(v);
        var r = new NetReader(w.Span);
        Assert.Equal(v, r.ReadVarInt());
        Assert.True(r.AtEnd);
    }

    [Fact]
    public void Primitives_And_Strings_RoundTrip()
    {
        var w = new NetWriter(4);
        w.WriteByte(7); w.WriteUShort(65000); w.WriteInt(-5); w.WriteLong(1L << 40); w.WriteFloat(1.5f);
        w.WriteString("Rook 한글"); w.WritePosition(new Vec3d(12.346, -0.5, 1000.009));
        var r = new NetReader(w.Span);
        Assert.Equal(7, r.ReadByte());
        Assert.Equal(65000, r.ReadUShort());
        Assert.Equal(-5, r.ReadInt());
        Assert.Equal(1L << 40, r.ReadLong());
        Assert.Equal(1.5f, r.ReadFloat());
        Assert.Equal("Rook 한글", r.ReadString());
        var p = r.ReadPosition();
        Assert.Equal(12.35, p.X, 6); Assert.Equal(-0.5, p.Y, 6); Assert.Equal(1000.01, p.Z, 6);
    }

    [Fact]
    public void Truncated_Input_Throws_FormatException()
    {
        var bytes = Messages.SnapshotPacket(10, 3, new[] { new EntityState(1, ActorKind.Player, Vec3d.Zero, Vec3d.Zero, 0, 0, 0, 100, 0) });
        Assert.Throws<NetFormatException>(() =>
        {
            var r = new NetReader(bytes.AsSpan(0, bytes.Length - 2));
            r.ReadByte();
            Messages.ReadSnapshot(ref r);
        });
    }

    [Fact]
    public void Hostile_Count_Is_Rejected()
    {
        var w = new NetWriter();
        w.WriteVarUInt(1); w.WriteVarUInt(1); w.WriteVarUInt(1_000_000);
        Assert.Throws<NetFormatException>(() => { var r = new NetReader(w.Span); Messages.ReadSnapshot(ref r); });
    }

    [Fact]
    public void Input_Quantization_Is_Stable()
    {
        var cmd = InputCmd.Create(5, 2, 0.5, -1, 1.0, -0.3, InputButtons.Fire | InputButtons.Aim, 2);
        var w = new NetWriter();
        cmd.Write(w);
        var r = new NetReader(w.Span);
        Assert.Equal(cmd, InputCmd.Read(ref r));
        Assert.Equal(1.0, cmd.YawRadians, 3);
        Assert.Equal(-0.3, cmd.PitchRadians, 3);
        Assert.Equal(-1.0, cmd.Forward, 6);
    }

    [Fact]
    public void Snapshot_Of_16_Players_Fits_Budget()
    {
        var ents = Enumerable.Range(1, 16).Select(i => new EntityState(i, ActorKind.Player,
            new Vec3d(i * 13.37, 0, -i * 7.1), new Vec3d(3, 0, -2), i, 0.1, ActorFlags.Aiming, 100, 50)).ToList();
        var bytes = Messages.SnapshotPacket(123456, 99999, ents);
        // 20 Hz × size must stay under the 64 kbps per-player downstream target with headroom
        Assert.True(bytes.Length * 20 * 8 < 64_000, $"snapshot {bytes.Length} bytes");
    }
}
