using Harbor.Core.Data;
using Harbor.Core.Math;

namespace Harbor.Core.Economy;

/// <summary>data/economy/economy.json.</summary>
public sealed record EconomyConfig
{
    public StartingDef StartingBalance { get; init; } = new();
    public DeathDropDef DeathDrop { get; init; } = new();
    public double ArrestFineFraction { get; init; } = 0.4;
    public double LogoutOutsideSafehouseTax { get; init; } = 0.1;
    public double InstantLaunderFee { get; init; } = 0.45;
    public DiminishingDef DiminishingReturns { get; init; } = new();
    public BountyDef Bounty { get; init; } = new();

    public sealed record StartingDef(long Clean = 2000, long Dirty = 0);
    public sealed record DeathDropDef(double Fraction = 0.25, long Min = 100, long Max = 25000, double CrewPrioritySeconds = 30,
        double LifetimeSeconds = 300, double PickupRadius = 1.5);
    public sealed record DiminishingDef(int StartAtRepeat = 3, double StepPenalty = 0.2, double Floor = 0.4, double WindowSeconds = 3600);
    public sealed record BountyDef(int MinHeatLevel = 4, double HoldSeconds = 60, double WalletFraction = 0.2, long Base = 2000);
}

public enum ShopItemKind { Weapon, Ammo, Armor, Medkit, Mod }

/// <summary>data/economy/shop.json entry. Prices are server-side truth; clients only send item ids.</summary>
public sealed record ShopItem
{
    public string Id { get; init; } = "";
    public ShopItemKind Kind { get; init; }
    public string Ref { get; init; } = "";
    public int Amount { get; init; } = 1;
    public long Price { get; init; }
    public List<Currency> Currencies { get; init; } = new();
    public int MinRep { get; init; }
}

public sealed record BusinessDef
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string District { get; init; } = "";
    public double FeeRate { get; init; }
    public long ThroughputPerHour { get; init; }
    public long Price { get; init; }
    public double DailyUpkeepRate { get; init; }
}

public sealed record SafehouseDef
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string District { get; init; } = "";
    public double[] Position { get; init; } = new double[3];
    public double Radius { get; init; } = 20;
    public long Price { get; init; }
    public Vec3d Center => new(Position[0], Position[1], Position[2]);
    public bool Contains(Vec3d p) => (p - Center).WithY(0).Length <= Radius;
}

public sealed record EconomyData(EconomyConfig Config, IReadOnlyDictionary<string, ShopItem> Shop,
    IReadOnlyDictionary<string, BusinessDef> Businesses, IReadOnlyList<SafehouseDef> Safehouses)
{
    public static EconomyData Default => new(new EconomyConfig(), new Dictionary<string, ShopItem>(),
        new Dictionary<string, BusinessDef>(), Array.Empty<SafehouseDef>());

    internal void Validate()
    {
        foreach (var i in Shop.Values)
            Json.Require(i.Price >= 0 && i.Currencies.Count > 0 && i.Amount >= 1, $"shop item {i.Id} invalid");
        foreach (var b in Businesses.Values)
            Json.Require(b.FeeRate is >= 0 and < 1 && b.ThroughputPerHour > 0, $"business {b.Id} invalid");
        Json.Require(Config.DeathDrop.Fraction is >= 0 and <= 1, "deathDrop.fraction must be 0..1");
    }
}
