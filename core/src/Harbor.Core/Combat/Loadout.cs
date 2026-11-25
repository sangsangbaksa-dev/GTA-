namespace Harbor.Core.Combat;

public sealed class WeaponSlot(WeaponDef def)
{
    public WeaponDef Def { get; } = def;
    public int InMagazine { get; internal set; } = def.MagazineSize;
}

/// <summary>Per-actor weapons and ammo. Reserve ammo is per weapon class and costs money (economy sink).</summary>
public sealed class Loadout
{
    public const int MaxSlots = 4;
    public const double SwapSeconds = 0.4;

    public List<WeaponSlot> Slots { get; } = new();
    public Dictionary<WeaponClass, int> Reserve { get; } = new();
    public int Active { get; internal set; }
    public double Cooldown { get; internal set; }
    public double ReloadLeft { get; internal set; }
    internal bool TriggerWasHeld;

    public WeaponSlot? Current => Active < Slots.Count ? Slots[Active] : null;
    public bool IsReloading => ReloadLeft > 0;

    public int ReserveOf(WeaponClass c) => Reserve.GetValueOrDefault(c);

    public bool Has(string weaponId) => Slots.Any(s => s.Def.Id == weaponId);

    public bool Add(WeaponDef def)
    {
        if (Has(def.Id) || Slots.Count >= MaxSlots) return false;
        Slots.Add(new WeaponSlot(def));
        return true;
    }
}
