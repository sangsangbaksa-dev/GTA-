using Harbor.Core.Data;
using Harbor.Core.Economy;
using Harbor.Core.Math;
using Harbor.Core.Missions;

namespace Harbor.Core.Activities;

public enum ContractKind { Contract, HeistPrep, HeistFinale }

public sealed record RewardDef(Currency Currency = Currency.Dirty, long Min = 0, long Max = 0, long Xp = 0);

/// <summary>data/activities/contracts/*.json — one file per contract (heist preps and finales included).</summary>
public sealed record ContractDef
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public ContractKind Kind { get; init; }
    public string District { get; init; } = "";
    public int MinRep { get; init; }
    public int MinPlayers { get; init; } = 1;
    public int MaxPlayers { get; init; } = 4;
    public double TimeLimitSeconds { get; init; } = 600;
    public double CooldownSeconds { get; init; } = 120;
    /// <summary>Online crew mates within this distance of the starter join the run.</summary>
    public double PartyRadius { get; init; } = 60;
    public RewardDef Reward { get; init; } = new();
    public List<ObjectiveDef> Objectives { get; init; } = new();

    public void Validate()
    {
        Json.Require(Id.Length > 0, "contract id missing");
        Json.Require(Objectives.Count > 0, $"{Id}: no objectives");
        Json.Require(MinPlayers >= 1 && MaxPlayers >= MinPlayers && MaxPlayers <= 4, $"{Id}: players must be 1..4");
        Json.Require(Reward.Min >= 0 && Reward.Max >= Reward.Min, $"{Id}: reward max must be >= min");
        Json.Require(TimeLimitSeconds > 0, $"{Id}: timeLimitSeconds must be > 0");
        foreach (var o in Objectives) o.Validate(Id);
        if (Kind == ContractKind.HeistFinale)
            Json.Require(Objectives.Any(o => o.Type == ObjectiveType.Deliver && o.ValueEach > 0), $"{Id}: finale needs a deliver objective with value");
    }
}

/// <summary>data/activities/heists.json.</summary>
public sealed record HeistDef
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string District { get; init; } = "";
    public List<string> Preps { get; init; } = new();
    public string Finale { get; init; } = "";
    public int MinPlayers { get; init; } = 2;
    public int MinSharePercent { get; init; } = 15;
    public double SplitTimeoutSeconds { get; init; } = 120;
    public int CrewRep { get; init; } = 1;
}

public sealed record TurfZoneDef
{
    public string Id { get; init; } = "";
    public string District { get; init; } = "";
    public double[] At { get; init; } = new double[3];
    public double Radius { get; init; } = 15;
    public long IncomePerHour { get; init; } = 6000;
    public Vec3d Center => At.ToVec();
}

/// <summary>data/activities/turf.json.</summary>
public sealed record TurfConfig
{
    public double CaptureSeconds { get; init; } = 60;
    public double ShopDiscount { get; init; } = 0.10;
    public double AttackIntervalSeconds { get; init; } = 1800;
    public double AttackNoticeSeconds { get; init; } = 120;
    public double AttackWindowSeconds { get; init; } = 300;
    public double PayoutIntervalSeconds { get; init; } = 60;
    public List<TurfZoneDef> Zones { get; init; } = new();
}

public sealed record SupplySiteDef(string Id, string District, double[] At);

/// <summary>data/activities/supply_drops.json.</summary>
public sealed record SupplyDropConfig
{
    public double IntervalSeconds { get; init; } = 900;
    public double FirstDropSeconds { get; init; } = 300;
    public double NoticeSeconds { get; init; } = 60;
    public double OpenSeconds { get; init; } = 10;
    public double OpenRadius { get; init; } = 2;
    public double LifetimeSeconds { get; init; } = 300;
    public long Cash { get; init; } = 6000;
    public List<string> Items { get; init; } = new();
    public List<SupplySiteDef> Sites { get; init; } = new();
}

public sealed record ActivityData(
    IReadOnlyDictionary<string, ContractDef> Contracts,
    IReadOnlyDictionary<string, HeistDef> Heists,
    TurfConfig Turf,
    SupplyDropConfig SupplyDrops)
{
    public static ActivityData Empty => new(new Dictionary<string, ContractDef>(), new Dictionary<string, HeistDef>(), new TurfConfig(), new SupplyDropConfig());

    public HeistDef? HeistOf(string contractId) =>
        Heists.Values.FirstOrDefault(h => h.Finale == contractId || h.Preps.Contains(contractId));

    public void Validate(EconomyData economy)
    {
        foreach (var c in Contracts.Values) c.Validate();
        foreach (var h in Heists.Values)
        {
            Json.Require(Contracts.TryGetValue(h.Finale, out var f) && f.Kind == ContractKind.HeistFinale, $"heist {h.Id}: finale {h.Finale} missing or not a finale");
            foreach (var p in h.Preps)
                Json.Require(Contracts.TryGetValue(p, out var c) && c.Kind == ContractKind.HeistPrep, $"heist {h.Id}: prep {p} missing or not a prep");
            Json.Require(h.MinSharePercent * System.Math.Max(h.MinPlayers, 4) <= 100, $"heist {h.Id}: minSharePercent too high for 4 players");
        }
        Json.Require(Turf.Zones.Select(z => z.Id).Distinct().Count() == Turf.Zones.Count, "turf: duplicate zone id");
        Json.Require(Turf.ShopDiscount is >= 0 and < 1, "turf.shopDiscount must be 0..1");
        Json.Require(SupplyDrops.NoticeSeconds < SupplyDrops.IntervalSeconds, "supply drop notice must be shorter than interval");
        foreach (var i in SupplyDrops.Items)
            Json.Require(economy.Shop.ContainsKey(i), $"supply drop item {i} is not a shop item");
    }
}
