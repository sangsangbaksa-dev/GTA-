namespace Harbor.Core.Net;

[Flags]
public enum InputButtons : ushort
{
    None = 0,
    Sprint = 1 << 0,
    Crouch = 1 << 1,
    Aim = 1 << 2,
    Fire = 1 << 3,
    Reload = 1 << 4,
    Interact = 1 << 5,
    Jump = 1 << 6,
    Melee = 1 << 7,
}

/// <summary>One fixed tick of player input — the only thing a client sends about its avatar.
/// Fields are stored already quantized so the client predicts with exactly what the server receives.</summary>
/// <param name="Sequence">Client input counter, acked by the server in snapshots.</param>
/// <param name="ViewTick">Server tick the client was rendering (interpolation time) — used for lag compensation.</param>
public readonly record struct InputCmd(
    uint Sequence, uint ViewTick, sbyte MoveRight, sbyte MoveForward,
    ushort Yaw, ushort Pitch, InputButtons Buttons, byte WeaponSlot)
{
    public static InputCmd Create(uint sequence, uint viewTick, double moveRight, double moveForward,
        double yaw, double pitch, InputButtons buttons, byte weaponSlot = 0)
        => new(sequence, viewTick, Quant.Axis(moveRight), Quant.Axis(moveForward),
            Quant.Angle(yaw), Quant.Angle(System.Math.Clamp(pitch, -1.45, 1.45)), buttons, weaponSlot);

    public double Right => Quant.FromAxis(MoveRight);
    public double Forward => Quant.FromAxis(MoveForward);
    public double YawRadians => Quant.FromAngle(Yaw);
    public double PitchRadians => Quant.FromAngleSigned(Pitch);
    public bool Has(InputButtons b) => (Buttons & b) == b;

    public void Write(NetWriter w)
    {
        w.WriteVarUInt(Sequence);
        w.WriteVarUInt(ViewTick);
        w.WriteSByte(MoveRight);
        w.WriteSByte(MoveForward);
        w.WriteUShort(Yaw);
        w.WriteUShort(Pitch);
        w.WriteUShort((ushort)Buttons);
        w.WriteByte(WeaponSlot);
    }

    public static InputCmd Read(ref NetReader r)
        => new(r.ReadVarUInt32(), r.ReadVarUInt32(), r.ReadSByte(), r.ReadSByte(),
            r.ReadUShort(), r.ReadUShort(), (InputButtons)r.ReadUShort(), r.ReadByte());
}
