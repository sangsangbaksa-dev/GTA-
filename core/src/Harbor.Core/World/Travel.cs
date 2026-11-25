using Harbor.Core.Data;
using Harbor.Core.Math;

namespace Harbor.Core.World;

public sealed record TravelPoint(string Id, string DisplayName, double[] Position)
{
    public Vec3d Pos => new(Position[0], Position[1], Position[2]);
}

/// <summary>Taxi between districts (data/world/travel.json): costs clean money, blocked while wanted/busy.
/// Stands in for vehicles until M1 driving lands.</summary>
public sealed record TravelConfig
{
    public long Cost { get; init; } = 150;
    public double CooldownSeconds { get; init; } = 60;
    public List<TravelPoint> Points { get; init; } = new();

    public void Validate()
    {
        Json.Require(Cost >= 0 && CooldownSeconds >= 0, "travel: cost/cooldown must be >= 0");
        foreach (var p in Points) Json.Require(p.Position.Length == 3, $"travel point {p.Id}: position needs 3 numbers");
        Json.Require(Points.Select(p => p.Id).Distinct().Count() == Points.Count, "travel: duplicate point id");
    }
}
