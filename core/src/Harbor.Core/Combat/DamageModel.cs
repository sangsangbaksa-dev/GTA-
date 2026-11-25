using Harbor.Core.Sim;

namespace Harbor.Core.Combat;

public readonly record struct DamageResult(int HealthLost, int ArmorLost, bool ReachedZero);

/// <summary>Pure damage math (CONTENT_DESIGN §4.1–4.2).</summary>
public static class DamageModel
{
    public static double Falloff(WeaponDef w, double distance)
    {
        var r = w.RangeM;
        if (distance <= r.FalloffStart) return 1.0;
        if (distance >= r.FalloffEnd) return r.MinDamageScale;
        var k = (distance - r.FalloffStart) / (r.FalloffEnd - r.FalloffStart);
        return 1.0 + (r.MinDamageScale - 1.0) * k;
    }

    public static double ZoneMultiplier(WeaponDef w, HitZone zone, CombatConfig cfg) => zone switch
    {
        HitZone.Head => cfg.Zones.Head > 0 ? cfg.Zones.Head : w.HeadshotMultiplier,
        HitZone.Limbs => cfg.Zones.Limbs,
        _ => cfg.Zones.Torso,
    };

    /// <summary>Damage of one bullet/pellet before armor.</summary>
    public static double Raw(WeaponDef w, HitZone zone, double distance, double penetrationScale, CombatConfig cfg)
        => w.Damage * ZoneMultiplier(w, zone, cfg) * Falloff(w, distance) * penetrationScale;

    /// <summary>Applies damage to vitals. Armor absorbs a share (never on headshots); health loss is rounded, ≥1 when any damage.</summary>
    public static DamageResult Apply(ref Vitals v, double raw, bool bypassArmor, CombatConfig cfg)
    {
        if (raw <= 0 || v.Health <= 0) return new DamageResult(0, 0, v.Health <= 0);
        var absorbed = bypassArmor ? 0.0 : System.Math.Min(v.Armor, raw * cfg.Armor.Absorb);
        var armorLost = (int)System.Math.Round(absorbed);
        var healthLost = System.Math.Max(1, (int)System.Math.Round(raw - absorbed));
        healthLost = System.Math.Min(healthLost, v.Health);
        v.Armor -= System.Math.Min(armorLost, v.Armor);
        v.Health -= healthLost;
        v.SecondsSinceDamage = 0;
        return new DamageResult(healthLost, armorLost, v.Health <= 0);
    }
}
