using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Sim;
using Harbor.Core.Vehicles;
using Harbor.Core.World;
using M = System.Math;

namespace Harbor.Core.Server;

internal sealed class HarborFleet
{
    public const int StarterId = 1;
    private static readonly VehicleShape ExitProbe = new() { HalfWidth = 0.4, HalfLength = 0.4, RoofHeight = 1.8 };
    private readonly List<Vehicle> _vehicles = [];
    private readonly Dictionary<int, Vehicle> _drivers = [];
    private readonly StreetPopulation? _ambient;
    private readonly StaticWorld _level;
    private readonly SimWorld _world;
    private readonly VehicleCatalog _catalog;
    private readonly bool _useModelCatalog;

    public HarborFleet(HarborLockdownDef definition, StaticWorld level, SimWorld world, VehicleCatalog catalog,
        StreetPopulation? ambient, StreetPopulationDef? streets, bool useModelCatalog)
    {
        _world = world;
        _level = level;
        _catalog = catalog;
        _ambient = ambient;
        _useModelCatalog = useModelCatalog;
        Starter = Add(StarterId, catalog.Starter, HarborLockdownDef.Point(definition.Vehicle), 0, 0);
        if (useModelCatalog && streets != null)
        {
            foreach (var car in streets.ParkedVehicles)
                Add(_vehicles.Count + 1, catalog.Find(car.ModelId), StreetPopulationDef.ToPoint(car.Position),
                    car.YawDegrees * M.PI / 180, _vehicles.Count);
            foreach (var model in streets.TrafficRoutes.SelectMany(route => route.VehicleModels))
                catalog.Find(model);
        }
        if (ambient != null)
            ambient.VehicleObstacles = () => _vehicles.Select(car => (car.Body.Position, car.Model.Shape.HalfLength));
    }

    public Vehicle Starter { get; }
    public Vehicle? Driving(int actorId) => _drivers.GetValueOrDefault(actorId);
    public bool IsDriving(int actorId) => _drivers.ContainsKey(actorId);

    public void Step(double dt, long tick)
    {
        var traffic = Traffic().ToArray();
        foreach (var car in _vehicles)
        {
            if (car.DriverId >= 0 && (!_world.Actors.TryGet(car.DriverId, out var driver) || !driver.IsAlive))
                ClearDriver(car.DriverId);

            var body = car.Body;
            if (car.DriverId >= 0 || M.Abs(body.Speed) > 0)
            {
                var input = car.DriverId >= 0 && tick - car.InputTick <= 6 ? car.Input : default;
                body.Step(input, dt, _level, (position, yaw) =>
                    _vehicles.Any(other => car != other && ArcadeVehicle.Overlaps(position, yaw, body.Shape,
                        other.Body.Position, other.Body.Yaw, other.Body.Shape))
                    || traffic.Any(other => ArcadeVehicle.Overlaps(position, yaw, body.Shape,
                        other.Traffic.Position, other.Traffic.Yaw, other.Model.Shape)));
            }

            if (car.DriverId >= 0 && _world.Actors.TryGet(car.DriverId, out var actor))
                Seat(actor, car);
        }
    }

    public HarborVehicleState[] GetStates(Vec3d? observer = null)
    {
        var radius = _ambient?.VisibilityRadius ?? double.PositiveInfinity;
        bool Visible(Vec3d position) => observer is null || (position - observer.Value).WithY(0).LengthSquared <= radius * radius;
        return _vehicles.Where(car => Visible(car.Body.Position)).Select(car => car.State)
            .Concat(Traffic().Where(car => Visible(car.Traffic.Position)).Select(car => TrafficState(car.Traffic, car.Model))).ToArray();
    }

    public (int Id, bool Occupied) EntryTarget(Actor player, double range, double takeoverSpeed, bool starterOnly)
    {
        if (!player.IsAlive || IsDriving(player.Id)) return (-1, false);
        var best = range * range;
        var result = (-1, false);
        foreach (var car in _vehicles)
        {
            if (car.DriverId < 0 && (!starterOnly || car == Starter) && M.Abs(car.Body.Speed) <= takeoverSpeed
                && CanReach(player.Position, car.Body.Position, ref best))
                result = (car.Id, false);
        }
        if (starterOnly) return result;

        foreach (var (car, model) in Traffic())
        {
            if (car.Speed > takeoverSpeed) continue;
            var candidate = best;
            if (!CanReach(player.Position, car.Position, ref candidate)
                || ExitPosition(car.Position, car.Yaw, model.Shape, car.VehicleId, player.Id) == null) continue;
            best = candidate;
            result = (car.VehicleId, true);
        }
        return result;
    }

    public bool Enter(Actor player, int vehicleId)
    {
        var vehicle = _vehicles.Find(car => car.Id == vehicleId);
        if (vehicle == null)
        {
            var (traffic, model) = Traffic().FirstOrDefault(car => car.Traffic.VehicleId == vehicleId);
            if (model == null) return false;
            var position = ExitPosition(traffic.Position, traffic.Yaw, model.Shape, vehicleId, player.Id);
            if (position == null) return false;
            var direction = (position.Value - traffic.Position).WithY(0).Normalized();
            _ambient!.TakeVehicle(traffic, position.Value, direction, _level);
            vehicle = Add(traffic.VehicleId, model, traffic.Position, traffic.Yaw, traffic.Appearance);
            vehicle.Body.Restore(traffic.Position, traffic.Yaw, traffic.Speed, traffic.Steering, traffic.Traveled);
        }
        if (vehicle.DriverId >= 0 || !_drivers.TryAdd(player.Id, vehicle)) return false;
        vehicle.DriverId = player.Id;
        Seat(player, vehicle);
        return true;
    }

    public bool Exit(Actor player, double maxSpeed)
    {
        if (!_drivers.TryGetValue(player.Id, out var car) || M.Abs(car.Body.Speed) > maxSpeed) return false;
        var position = ExitPosition(car.Body.Position, car.Body.Yaw, car.Body.Shape, car.Id, player.Id);
        if (position == null) return false;
        ClearDriver(player.Id);
        player.Position = position.Value;
        player.Move.Velocity = Vec3d.Zero;
        player.Move.Crouched = false;
        player.Move.Sprinting = false;
        return true;
    }

    public void ClearDriver(int id)
    {
        if (!_drivers.Remove(id, out var car)) return;
        car.DriverId = -1;
        car.Input = default;
        car.InputTick = -10;
    }

    public void Reset()
    {
        foreach (var driverId in _drivers.Keys.ToArray()) ClearDriver(driverId);
        _vehicles.RemoveAll(car => car.Id >= StreetPopulation.FirstTrafficId);
        foreach (var vehicle in _vehicles) vehicle.Body.Restore(vehicle.Spawn, vehicle.SpawnYaw, 0, 0, 0);
        _ambient?.Reset();
    }

    private static void Seat(Actor player, Vehicle car)
    {
        player.Position = car.Body.Position;
        player.Move.Velocity = Vec3d.FromYawPitch(car.Body.Yaw, 0) * car.Body.Speed;
        player.Move.Crouched = false;
        player.Move.Sprinting = false;
    }

    private Vehicle Add(int id, VehicleModel model, Vec3d position, double yaw, int appearance)
    {
        var car = new Vehicle(id, model, position, yaw, appearance);
        _vehicles.Add(car);
        return car;
    }

    private bool CanReach(Vec3d player, Vec3d vehicle, ref double best)
    {
        var delta = vehicle - player;
        var distance = delta.LengthSquared;
        if (distance >= best || _level.Raycast(player + Vec3d.Up, delta, delta.Length, out _)) return false;
        best = distance;
        return true;
    }

    private Vec3d? ExitPosition(Vec3d pivot, double yaw, VehicleShape shape, int vehicleId, int playerId)
    {
        var forward = Vec3d.FromYawPitch(yaw, 0);
        var right = new Vec3d(-forward.Z, 0, forward.X);
        var side = right * (shape.HalfWidth + 0.81);
        var back = forward * (-shape.HalfLength - 0.57);
        foreach (var offset in new[] { side, -side, back })
        {
            var position = pivot + offset;
            var overlap = ArcadeVehicle.Blocked(_level, position, 0, ExitProbe)
                || _level.Raycast(pivot + Vec3d.Up, offset, offset.Length, out _)
                || _vehicles.Any(car => car.Id != vehicleId && ArcadeVehicle.Overlaps(position, 0, ExitProbe,
                    car.Body.Position, car.Body.Yaw, car.Body.Shape))
                || Traffic().Any(car => car.Traffic.VehicleId != vehicleId && ArcadeVehicle.Overlaps(position, 0, ExitProbe,
                    car.Traffic.Position, car.Traffic.Yaw, car.Model.Shape))
                || _world.Actors.All.Any(actor => actor.Id != playerId && actor.IsAlive
                    && (actor.Position - position).LengthSquared < 0.85 * 0.85);
            if (!overlap) return position;
        }
        return null;
    }

    private IEnumerable<(StreetTrafficState Traffic, VehicleModel Model)> Traffic() =>
        _ambient?.Traffic.Select(car => (car, _catalog.ForTraffic(car.Appearance, _useModelCatalog ? car.ModelId : null))) ?? [];

    private static HarborVehicleState TrafficState(StreetTrafficState car, VehicleModel model) =>
        Describe(new HarborVehicleState(car.Position, car.Yaw, car.Speed, car.Steering, car.Traveled), car.VehicleId,
            model, car.Appearance, driverId: -1, occupied: true);

    private static HarborVehicleState Describe(HarborVehicleState state, int id, VehicleModel model, int appearance, int driverId, bool occupied) =>
        state with
        {
            Id = id, ModelId = model.Id, VehicleClass = model.Class, DisplayName = model.DisplayName,
            Appearance = appearance, DriverId = driverId, Occupied = occupied, HalfWidth = model.Shape.HalfWidth,
            HalfLength = model.Shape.HalfLength, RoofHeight = model.Shape.RoofHeight, WheelRadius = model.Shape.WheelRadius,
            Wheelbase = model.Shape.Wheelbase,
        };

    internal sealed class Vehicle(int id, VehicleModel model, Vec3d spawn, double yaw, int appearance)
    {
        public int Id { get; } = id;
        public VehicleModel Model { get; } = model;
        public Vec3d Spawn { get; } = spawn;
        public double SpawnYaw { get; } = yaw;
        public ArcadeVehicle Body { get; } = new(spawn, yaw, model.Arcade, model.Shape);
        public int DriverId { get; set; } = -1;
        public VehicleInput Input { get; set; }
        public long InputTick { get; set; } = -10;
        public HarborVehicleState State => Describe(new HarborVehicleState(Body.Position, Body.Yaw, Body.Speed, Body.Steering, Body.Distance),
            Id, Model, appearance, DriverId, DriverId >= 0);
    }
}
