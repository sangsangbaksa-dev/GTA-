using Harbor.Core.Data;
using Harbor.Core.Geometry;

namespace Harbor.Core.Combat;

/// <summary>data/combat/combat.json.</summary>
public sealed record CombatConfig
{
    public HealthDef Health { get; init; } = new();
    public ArmorDef Armor { get; init; } = new();
    public ZonesDef Zones { get; init; } = new();
    public DownedDef Downed { get; init; } = new();
    public double RespawnSeconds { get; init; } = 10;
    public SuppressionDef Suppression { get; init; } = new();
    public ExplosiveDef Explosive { get; init; } = new();
    public double EyeHeight { get; init; } = 1.6;
    public double EyeHeightCrouched { get; init; } = 1.1;
    public double RepeatKillWindowSeconds { get; init; } = 600;
    public int RepeatKillLimit { get; init; } = 3;

    public sealed record HealthDef(int Max = 100, double RegenDelaySeconds = 6, double RegenPerSecond = 8, int RegenCap = 50);
    public sealed record ArmorDef(int Max = 100, double Absorb = 0.6);
    /// <summary>Head = 0 means "use the weapon's headshotMultiplier".</summary>
    public sealed record ZonesDef(double Head = 0, double Torso = 1.0, double Limbs = 0.75);
    public sealed record DownedDef(double Seconds = 20, double ReviveSeconds = 3, int ReviveHealth = 30, double ReviveRange = 1.8);
    public sealed record SuppressionDef(double Radius = 1.5, double PerShot = 0.25, double DecayPerSecond = 0.5, double SpreadMultiplierAtMax = 1.5);
    public sealed record ExplosiveDef(int Health = 30, double Radius = 5, double InnerDamage = 120, double OuterDamage = 20, double ChainDelaySeconds = 0.2);
}

public sealed record MaterialRule(SurfaceMaterial Material, IReadOnlySet<WeaponClass> PenetrableBy, double DamageScale, double MaxThickness);

/// <summary>data/combat/materials.json — which weapon classes pass through which surfaces.</summary>
public sealed class MaterialTable
{
    private readonly Dictionary<SurfaceMaterial, MaterialRule> _rules = new();

    public MaterialTable(IEnumerable<MaterialRule> rules)
    {
        foreach (var r in rules) _rules[r.Material] = r;
    }

    public static MaterialTable Default => new(new[]
    {
        new MaterialRule(SurfaceMaterial.Wood, new HashSet<WeaponClass> { WeaponClass.Rifle }, 0.5, 1.3),
        new MaterialRule(SurfaceMaterial.ThinMetal, new HashSet<WeaponClass> { WeaponClass.Rifle, WeaponClass.Smg }, 0.6, 1.0),
        new MaterialRule(SurfaceMaterial.Glass, new HashSet<WeaponClass> { WeaponClass.Pistol, WeaponClass.Smg, WeaponClass.Rifle, WeaponClass.Shotgun }, 0.85, 0.2),
    });

    /// <summary>Damage scale after passing <paramref name="thickness"/> m of material, or 0 if the bullet stops.</summary>
    public double PassScale(SurfaceMaterial m, WeaponClass c, double thickness)
        => _rules.TryGetValue(m, out var r) && r.PenetrableBy.Contains(c) && thickness <= r.MaxThickness ? r.DamageScale : 0.0;

    public static SurfaceMaterial ParseId(string id) => id switch
    {
        "concrete" => SurfaceMaterial.Concrete,
        "metal" => SurfaceMaterial.Metal,
        "thin_metal" => SurfaceMaterial.ThinMetal,
        "wood" => SurfaceMaterial.Wood,
        "glass" => SurfaceMaterial.Glass,
        "earth" => SurfaceMaterial.Earth,
        _ => throw new DataException($"unknown material '{id}'"),
    };
}
