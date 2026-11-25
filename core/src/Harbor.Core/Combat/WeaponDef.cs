using Harbor.Core.Data;

namespace Harbor.Core.Combat;

public enum WeaponClass { Pistol, Smg, Rifle, Shotgun, Melee }
public enum FireMode { Semi, Auto, Pump }

public sealed record FalloffDef(double FalloffStart, double FalloffEnd, double MinDamageScale);
public sealed record SpreadDef(double Hip = 2.0, double Aim = 0.5);
public sealed record RecoilDef(double PitchDeg = 1.0, double YawDeg = 0.5);

/// <summary>data/weapons/*.json (schema: data/schema/weapon.schema.json).</summary>
public sealed record WeaponDef
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public WeaponClass Class { get; init; }
    public double Damage { get; init; }
    public int Pellets { get; init; } = 1;
    public double HeadshotMultiplier { get; init; } = 2.0;
    public FireMode FireMode { get; init; }
    public double RoundsPerMinute { get; init; }
    public int MagazineSize { get; init; }
    public double ReloadSeconds { get; init; }
    public FalloffDef RangeM { get; init; } = new(10, 30, 0.5);
    public SpreadDef SpreadDeg { get; init; } = new();
    public RecoilDef Recoil { get; init; } = new();
    public double NoiseRadiusM { get; init; } = 50;
    public string ModelAsset { get; init; } = "";

    public double SecondsPerShot => 60.0 / RoundsPerMinute;
    public double MaxRange => RangeM.FalloffEnd * 2.5;

    public void Validate()
    {
        Json.Require(Id.Length > 0, "weapon without id");
        Json.Require(Damage > 0 && Pellets >= 1 && RoundsPerMinute > 0 && MagazineSize >= 1, $"weapon {Id}: invalid numbers");
        Json.Require(RangeM.FalloffEnd >= RangeM.FalloffStart && RangeM.MinDamageScale is >= 0 and <= 1, $"weapon {Id}: invalid range");
    }
}
