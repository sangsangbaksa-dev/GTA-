using Harbor.Core.Combat;
using Harbor.Core.Economy;
using Harbor.Core.Ports;

namespace Harbor.Core.Data;

/// <summary>Everything loaded from data/. Validated on load so bad data fails at startup, not mid-match.</summary>
public sealed class GameData
{
    public IReadOnlyDictionary<string, WeaponDef> Weapons { get; init; } = new Dictionary<string, WeaponDef>();
    public CombatConfig Combat { get; init; } = new();
    public MaterialTable Materials { get; init; } = MaterialTable.Default;
    public EconomyData Economy { get; init; } = EconomyData.Default;

    private sealed record MaterialFile(List<MaterialEntry> Materials);
    private sealed record ShopFile(List<ShopItem> Items);
    private sealed record BusinessFile(List<BusinessDef> Businesses);
    private sealed record SafehouseFile(List<SafehouseDef> Safehouses);
    private sealed record MaterialEntry(string Id, List<WeaponClass> PenetrableBy, double DamageScale, double MaxThickness);

    public static GameData Load(IFileSystem fs, string root = "data")
    {
        var weapons = new Dictionary<string, WeaponDef>();
        foreach (var path in fs.ListFiles($"{root}/weapons", ".json"))
        {
            var w = Json.Read<WeaponDef>(fs, path);
            w.Validate();
            Json.Require(weapons.TryAdd(w.Id, w), $"duplicate weapon id {w.Id}");
        }
        var mats = Json.Read<MaterialFile>(fs, $"{root}/combat/materials.json");
        var table = new MaterialTable(mats.Materials.Select(m =>
            new MaterialRule(MaterialTable.ParseId(m.Id), m.PenetrableBy.ToHashSet(), m.DamageScale, m.MaxThickness)));
        var combat = Json.Read<CombatConfig>(fs, $"{root}/combat/combat.json");
        Json.Require(combat.Armor.Absorb is >= 0 and <= 1, "combat.armor.absorb must be 0..1");
        var economy = new EconomyData(
            Json.Read<EconomyConfig>(fs, $"{root}/economy/economy.json"),
            Json.Read<ShopFile>(fs, $"{root}/economy/shop.json").Items.ToDictionary(i => i.Id),
            Json.Read<BusinessFile>(fs, $"{root}/economy/businesses.json").Businesses.ToDictionary(b => b.Id),
            Json.Read<SafehouseFile>(fs, $"{root}/world/safehouses.json").Safehouses);
        economy.Validate();
        foreach (var i in economy.Shop.Values.Where(i => i.Kind == ShopItemKind.Weapon))
            Json.Require(weapons.ContainsKey(i.Ref), $"shop item {i.Id} references unknown weapon {i.Ref}");
        return new GameData { Weapons = weapons, Combat = combat, Materials = table, Economy = economy };
    }
}
