using Harbor.Core.Data;
using Harbor.Core.Ports;

namespace Harbor.Core.Vehicles;

public sealed record VehicleShape
{
    public double HalfWidth { get; init; } = ArcadeVehicle.HalfWidth;
    public double HalfLength { get; init; } = ArcadeVehicle.HalfLength;
    public double RoofHeight { get; init; } = ArcadeVehicle.RoofHeight;
    public double WheelRadius { get; init; } = ArcadeVehicle.WheelRadius;
    public double Wheelbase { get; init; } = ArcadeVehicle.Wheelbase;

    internal void Validate()
    {
        Json.Require(HalfWidth is >= 0.5 and <= 1.5 && HalfLength is >= 1 and <= 3.5 && RoofHeight is >= 0.8 and <= 3
            && WheelRadius is >= 0.15 and <= 0.6 && Wheelbase > WheelRadius * 2 && Wheelbase <= (HalfLength - WheelRadius) * 2,
            "Invalid vehicle dimensions");
    }
}

public sealed record VehicleModel
{
    public const string StarterId = "calloway_compact";
    public string Id { get; init; } = StarterId;
    public string Class { get; init; } = "compact";
    public string DisplayName { get; init; } = "Calloway Pip";
    public ArcadeVehicleTuning Arcade { get; init; } = new();
    public VehicleShape Shape { get; init; } = new();
}

public sealed class VehicleCatalog
{
    private readonly Dictionary<string, VehicleModel> _models;
    private readonly VehicleModel[] _traffic;

    private VehicleCatalog(ArcadeVehicleTuning starter, IEnumerable<VehicleModel> models)
    {
        _models = models.ToDictionary(model => model.Id, StringComparer.Ordinal);
        if (!_models.ContainsKey(VehicleModel.StarterId))
            _models.Add(VehicleModel.StarterId, new VehicleModel { Arcade = starter });
        Starter = _models[VehicleModel.StarterId];
        _traffic = _models.Values.OrderBy(model => model.Id, StringComparer.Ordinal).ToArray();
    }

    public VehicleModel Starter { get; }

    public static VehicleCatalog Load(IFileSystem files, string directory, ArcadeVehicleTuning starter)
    {
        var models = new Dictionary<string, VehicleModel>(StringComparer.Ordinal);
        foreach (var path in files.ListFiles(directory, ".json"))
        {
            var model = Json.Read<VehicleModel>(files, path);
            Json.Require(!string.IsNullOrWhiteSpace(model.Id) && !string.IsNullOrWhiteSpace(model.Class)
                && !string.IsNullOrWhiteSpace(model.DisplayName) && model.Arcade != null && model.Shape != null
                && models.TryAdd(model.Id, model), $"{path}: invalid or duplicate vehicle model");
            model.Arcade!.Validate();
            model.Shape!.Validate();
        }
        return new VehicleCatalog(starter, models.Values);
    }

    internal static VehicleCatalog Default(ArcadeVehicleTuning starter) => new(starter, []);

    internal VehicleModel ForTraffic(int index, string? id) => id == null ? _traffic[index % _traffic.Length] : Find(id);

    internal VehicleModel Find(string id) =>
        _models.TryGetValue(id, out var model) ? model : throw new DataException($"Unknown vehicle model '{id}'");
}
