using Harbor.Core.Combat;
using Harbor.Core.Data;
using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Ports;
using Harbor.Core.Sim;
using M = System.Math;

namespace Harbor.Core.World;

public sealed record StreetRouteDef
{
    public string Id { get; init; } = "";
    public int Count { get; init; }
    public double Speed { get; init; }
    public double PauseSeconds { get; init; }
    public bool Loop { get; init; }
    public double[][] Points { get; init; } = [];
    public string[] VehicleModels { get; init; } = [];
    public StreetSignalDef[] Signals { get; init; } = [];
}

public sealed record StreetSignalDef(int Waypoint, double CycleSeconds, double GreenSeconds, double OffsetSeconds = 0);

public sealed record ParkedStreetVehicleDef(string ModelId, double[] Position, double YawDegrees = 0);

public sealed record StreetCargoDef(string Id, double[] Position, int Cash, double RefillSeconds = 90);

public sealed record StreetPopulationDef
{
    public const int MaxPedestrians = 48;
    public const int MaxTraffic = 32;
    public int Seed { get; init; } = 7219;
    public double VisibilityRadius { get; init; } = 140;
    public double HearingRadius { get; init; } = 23;
    public double FleeSeconds { get; init; } = 5;
    public double FleeSpeed { get; init; } = 3.9;
    public double PickpocketRadius { get; init; } = 1.8;
    public double PickpocketCooldownSeconds { get; init; } = 55;
    public int PickpocketMinCash { get; init; } = 12;
    public int PickpocketMaxCash { get; init; } = 32;
    public double TrafficAcceleration { get; init; } = 2.6;
    public double TrafficBraking { get; init; } = 6;
    public double TrafficLookAhead { get; init; } = 13;
    public ParkedStreetVehicleDef[] ParkedVehicles { get; init; } = [];
    public StreetCargoDef[] Cargo { get; init; } = [];
    public double CargoInteractionRadius { get; init; } = 2.15;
    public StreetRouteDef[] PedestrianRoutes { get; init; } = [];
    public StreetRouteDef[] TrafficRoutes { get; init; } = [];

    public static StreetPopulationDef Load(IFileSystem files, string path)
    {
        var def = Json.Read<StreetPopulationDef>(files, path);
        Json.Require(def.VisibilityRadius is > 0 and <= 400 && def.HearingRadius is > 0 and <= 100
            && def.FleeSeconds is > 0 and <= 30 && def.FleeSpeed is > 0 and <= 12
            && def.PickpocketRadius is > 0 and <= 4 && def.PickpocketCooldownSeconds is > 0 and <= 600
            && def.PickpocketMinCash > 0 && def.PickpocketMaxCash >= def.PickpocketMinCash && def.PickpocketMaxCash <= 1000
            && def.TrafficAcceleration is > 0 and <= 12 && def.TrafficBraking is > 0 and <= 20
            && def.TrafficLookAhead is >= 8 and <= 30, $"{path}: invalid population tuning");
        Json.Require(def.ParkedVehicles is { Length: <= 32 } && def.ParkedVehicles.All(car => car is not null
            && !string.IsNullOrWhiteSpace(car.ModelId) && ValidPoint(car.Position) && car.YawDegrees is >= -360 and <= 360),
            $"{path}: invalid parked vehicles");
        Json.Require(def.CargoInteractionRadius is > 0 and <= 4 && def.Cargo is { Length: <= 32 }
            && def.Cargo.All(cargo => cargo is not null && !string.IsNullOrWhiteSpace(cargo.Id) && ValidPoint(cargo.Position)
                && cargo.Cash is > 0 and <= 1000 && cargo.RefillSeconds is > 0 and <= 1800)
            && def.Cargo.Select(cargo => cargo.Id).Distinct(StringComparer.Ordinal).Count() == def.Cargo.Length,
            $"{path}: invalid street cargo");
        ValidateRoutes(def.PedestrianRoutes, MaxPedestrians, 4, false, path);
        ValidateRoutes(def.TrafficRoutes, MaxTraffic, 30, true, path);
        return def;
    }

    private static void ValidateRoutes(StreetRouteDef[]? routes, int maximum, double speedLimit, bool mustLoop, string path)
    {
        if (routes is null || routes.Length > maximum)
            throw new DataException($"{path}: missing route list or too many routes");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var population = 0;
        foreach (var route in routes)
        {
            if (route is null || string.IsNullOrWhiteSpace(route.Id) || !ids.Add(route.Id) ||
                route.Count < 1 || route.Count > maximum || (population += route.Count) > maximum ||
                !(route.Speed > 0 && route.Speed <= speedLimit) ||
                route.PauseSeconds is not (>= 0 and <= 12) ||
                (mustLoop && !route.Loop) || (route.Loop && route.PauseSeconds != 0))
                throw new DataException($"{path}: invalid route settings ({route?.Id})");

            if (route.Points is null || route.Points.Length > 64 || route.Points.Length < (route.Loop ? 3 : 2))
                throw new DataException($"{path}: invalid waypoint count ({route.Id})");

            foreach (var point in route.Points)
                if (!ValidPoint(point))
                    throw new DataException($"{path}: invalid waypoint ({route.Id})");

            Json.Require(route.VehicleModels is { Length: <= 8 } && route.VehicleModels.All(id => !string.IsNullOrWhiteSpace(id))
                && route.Signals is { Length: <= 12 } && (mustLoop || route.VehicleModels.Length + route.Signals.Length == 0)
                && route.Signals.All(signal => signal is not null && signal.Waypoint >= 0 && signal.Waypoint < route.Points.Length
                    && signal.CycleSeconds is >= 3 and <= 120 && signal.GreenSeconds > 0 && signal.GreenSeconds < signal.CycleSeconds
                    && signal.OffsetSeconds >= 0 && signal.OffsetSeconds < signal.CycleSeconds),
                $"{path}: invalid traffic options ({route.Id})");

            var points = route.Points.Select(ToPoint).ToArray();
            var segments = route.Loop ? points.Length : points.Length - 1;
            for (var i = 0; i < segments; i++)
                if ((points[i] - points[(i + 1) % points.Length]).WithY(0).LengthSquared < 0.25 * 0.25)
                    throw new DataException($"{path}: adjacent waypoints are too close ({route.Id})");
        }
    }

    private static bool ValidPoint(double[]? point) =>
        point is { Length: 3 } && point.All(coordinate => double.IsFinite(coordinate) && M.Abs(coordinate) <= 2000);

    internal static Vec3d ToPoint(double[] point) => new(point[0], point[1], point[2]);
}

public readonly record struct StreetPedestrianState(int Id, int Appearance, Vec3d Position, double Yaw, double Speed, bool Fleeing)
{
    public string Reaction { get; init; } = "waiting";
}

public readonly record struct StreetTrafficState(int Id, int Appearance, Vec3d Position, double Yaw, double Speed)
{
    public int VehicleId { get; init; }
    public string? ModelId { get; init; }
    internal double Traveled { get; init; }
    internal double Steering { get; init; }
}

public readonly record struct StreetCargoState(string Id, Vec3d Position, bool Available);

public sealed record StreetPopulationState
{
    public StreetPedestrianState[] Pedestrians { get; init; } = [];
    public StreetTrafficState[] Traffic { get; init; } = [];
    public StreetCargoState[] Cargo { get; init; } = [];
}

/// <summary>Authoritative ambient pedestrians and road traffic. Routes are baked from the visible map.
/// These agents live separately from combat actors and keep the same identities between snapshots.</summary>
public sealed class StreetPopulation(StreetPopulationDef definition) : ISimSystem
{
    internal const int FirstTrafficId = 1000;
    private Traveler[] _pedestrians = [];
    private Traveler[] _traffic = [];
    private readonly List<DisplacedDriver> _displaced = [];
    private CargoCache[] _cargo = [];
    private SimWorld _world = null!;
    private double _time;
    private int _nextDisplaced;
    internal Func<IEnumerable<(Vec3d Position, double Clearance)>>? VehicleObstacles { get; set; }
    public double VisibilityRadius => definition.VisibilityRadius;

    public void Initialize(SimWorld world)
    {
        _world = world;
        Reset();
        world.Events.Subscribe<ShotFired>(HearShot);
    }

    internal void Reset()
    {
        var random = new Random(definition.Seed);
        _pedestrians = CreateTravelers(definition.PedestrianRoutes, random, pedestrians: true);
        _traffic = CreateTravelers(definition.TrafficRoutes, random, pedestrians: false);
        _cargo = definition.Cargo.Select(cargo => new CargoCache(cargo)).ToArray();
        _displaced.Clear();
        _nextDisplaced = definition.PedestrianRoutes.Sum(route => route.Count);
        _time = 0;
    }

    public void Step(double dt)
    {
        _time += dt;
        foreach (var person in _pedestrians)
        {
            person.Panic = M.Max(0, person.Panic - dt);
            person.Startled = M.Max(0, person.Startled - dt);
            person.Pause = M.Max(0, person.Pause - dt);
            person.Speed = person.Startled > 0 ? 0 : person.Panic > 0 ? definition.FleeSpeed : person.Pause > 0 ? 0 : person.Cruise;
            person.Advance(person.Speed * dt);
        }

        foreach (var driver in _displaced)
        {
            driver.Remaining -= dt;
            var next = driver.Position + driver.Direction * (definition.FleeSpeed * dt);
            var probe = Aabb.FromCenter(next + new Vec3d(0, 0.85, 0), new Vec3d(0.65, 1.7, 0.65));
            if (driver.Level.Overlapping(probe).Any())
                driver.Direction = new Vec3d(-driver.Direction.Z, 0, driver.Direction.X);
            else
                driver.Position = next;
        }
        _displaced.RemoveAll(driver => driver.Remaining <= 0);

        var obstacles = VehicleObstacles?.Invoke().ToArray() ?? [];
        foreach (var car in _traffic)
        {
            if (car.Taken) continue;
            var wanted = CarTargetSpeed(car, obstacles);
            var change = wanted > car.Speed ? definition.TrafficAcceleration * dt : -definition.TrafficBraking * dt;
            car.Speed = wanted > car.Speed ? M.Min(wanted, car.Speed + change) : M.Max(wanted, car.Speed + change);
            var before = car.Yaw;
            car.Advance(car.Speed * dt);
            var turn = -M.IEEERemainder(car.Yaw - before, 2 * M.PI);
            car.Steering = M.Clamp(turn * 6, -0.38, 0.38);
        }
    }

    public StreetPopulationState GetState(Vec3d? observer = null)
    {
        var radiusSquared = definition.VisibilityRadius * definition.VisibilityRadius;
        bool Visible(Vec3d position) => observer is null || (position - observer.Value).WithY(0).LengthSquared <= radiusSquared;

        return new StreetPopulationState
        {
            Pedestrians = _pedestrians.Where(person => Visible(person.Position)).Select(person =>
                new StreetPedestrianState(person.Id, person.Appearance, Round(person.Position), M.Round(person.Yaw, 3),
                    M.Round(person.Speed, 2), person.Panic > 0 && person.Startled == 0)
                {
                    Reaction = person.Startled > 0 ? "startled" : person.Panic > 0 ? "fleeing" : person.Pause > 0 ? "waiting" : "walking",
                })
                .Concat(_displaced.Where(person => Visible(person.Position)).Select(person => new StreetPedestrianState(
                    person.Id, person.Appearance, Round(person.Position), M.Atan2(-person.Direction.X, -person.Direction.Z),
                    definition.FleeSpeed, true) { Reaction = "fleeing" })).ToArray(),
            Traffic = Traffic.Where(car => Visible(car.Position)).ToArray(),
            Cargo = _cargo.Where(cargo => Visible(cargo.Position))
                .Select(cargo => new StreetCargoState(cargo.Definition.Id, cargo.Position, cargo.ReadyAt <= _time)).ToArray()
        };
    }

    internal IEnumerable<StreetTrafficState> Traffic => _traffic.Where(car => !car.Taken).Select(car =>
        new StreetTrafficState(car.Id, car.Appearance, car.Position, car.Yaw, car.Speed)
        {
            VehicleId = FirstTrafficId + car.Id - 1, ModelId = car.ModelId, Traveled = car.Traveled, Steering = car.Steering,
        });

    internal void TakeVehicle(StreetTrafficState car, Vec3d driverPosition, Vec3d direction, StaticWorld level)
    {
        _traffic[car.Id - 1].Taken = true;
        _displaced.Add(new DisplacedDriver(++_nextDisplaced, car.Appearance, driverPosition, direction, definition.FleeSeconds, level));
        Alarm(driverPosition, definition.HearingRadius * 0.4, startled: true);
    }

    internal int PickpocketTarget(Vec3d player, StaticWorld level)
    {
        var nearest = _pedestrians.Where(person => person.Panic == 0 && person.PickpocketAfter <= _time
                && CanReach(player, person.Position, definition.PickpocketRadius, level))
            .MinBy(person => (person.Position - player).LengthSquared);
        return nearest?.Id ?? -1;
    }

    internal string CargoTarget(Vec3d player, StaticWorld level)
    {
        var nearest = _cargo.Where(cargo => cargo.ReadyAt <= _time
                && CanReach(player, cargo.Position, definition.CargoInteractionRadius, level))
            .MinBy(cargo => (cargo.Position - player).LengthSquared);
        return nearest?.Definition.Id ?? "";
    }

    internal int TakeCargo(string id)
    {
        var cargo = Array.Find(_cargo, cargo => cargo.Definition.Id == id);
        if (cargo == null || cargo.ReadyAt > _time) return 0;
        cargo.ReadyAt = _time + cargo.Definition.RefillSeconds;
        Alarm(cargo.Position, definition.HearingRadius * 0.25, startled: true);
        return cargo.Definition.Cash;
    }

    private static bool CanReach(Vec3d player, Vec3d point, double radius, StaticWorld level)
    {
        var delta = point - player;
        return delta.LengthSquared <= radius * radius && !level.Raycast(player + Vec3d.Up, delta, delta.Length, out _);
    }

    internal int Pickpocket(int id, Vec3d player, bool conspicuous)
    {
        var person = _pedestrians[id - 1];
        var max = definition.PickpocketMaxCash - definition.PickpocketMinCash + 1;
        if (!conspicuous)
            person.PickpocketAfter = _time + definition.PickpocketCooldownSeconds;
        Alarm(player, definition.PickpocketRadius * (conspicuous ? 3 : 1), startled: true);
        return conspicuous ? 0 : definition.PickpocketMinCash + (definition.Seed & int.MaxValue) % max * id % max;
    }

    private static Vec3d Round(Vec3d p) => new(M.Round(p.X, 2), M.Round(p.Y, 2), M.Round(p.Z, 2));

    private static Traveler[] CreateTravelers(StreetRouteDef[] routes, Random random, bool pedestrians)
    {
        var travelers = new List<Traveler>();
        foreach (var route in routes)
        {
            var track = new Track(route);
            for (var i = 0; i < route.Count; i++)
            {
                var id = travelers.Count + 1;
                var fraction = (i + 0.2 + 0.6 * random.NextDouble()) / route.Count;
                var cruise = pedestrians ? route.Speed * (0.88 + 0.24 * random.NextDouble()) : route.Speed;
                var direction = pedestrians && !route.Loop && i % 2 == 1 ? -1 : 1;
                var model = route.VehicleModels.Length == 0 ? null : route.VehicleModels[i % route.VehicleModels.Length];
                travelers.Add(new Traveler(id, (id - 1) % 4, track, fraction * track.Length, cruise, direction, model));
            }
        }
        return travelers.ToArray();
    }

    private void HearShot(ShotFired shot)
    {
        var radius = M.Min(shot.NoiseRadius, definition.HearingRadius);
        if (radius > 0) Alarm(shot.Origin, radius, startled: false);
    }

    private void Alarm(Vec3d origin, double radius, bool startled)
    {
        foreach (var person in _pedestrians)
        {
            var away = (person.Position - origin).WithY(0);
            if (away.LengthSquared > radius * radius) continue;
            if (away.Dot(person.Forward(1)) < 0) person.Direction *= -1;
            person.Panic = definition.FleeSeconds;
            person.Startled = startled ? 0.45 : 0;
            person.Pause = 0;
        }
    }

    private double CarTargetSpeed(Traveler car, (Vec3d Position, double Clearance)[] obstacles)
    {
        var wanted = car.Cruise;
        var heading = car.Forward(4);
        foreach (var actor in _world.Actors.All)
        {
            if (actor.Kind == ActorKind.Player && actor.IsAlive) Yield(actor.Position, 3.1, 1.9);
        }
        foreach (var person in _pedestrians) Yield(person.Position, 3.4, 1.65);
        foreach (var driver in _displaced) Yield(driver.Position, 3.4, 1.65);
        foreach (var obstacle in obstacles) Yield(obstacle.Position, 2.4 + obstacle.Clearance, 1.1 + obstacle.Clearance * 0.5);

        foreach (var ahead in _traffic)
        {
            if (ahead == car || ahead.Taken) continue;
            if (ahead.Path == car.Path)
            {
                var gap = car.Path.Wrap(ahead.Distance - car.Distance);
                if (gap < definition.TrafficLookAhead)
                    wanted = M.Min(wanted, M.Max(0, (gap - 5.4) * 0.9));
            }
            else if (ahead.Id < car.Id || ahead.Speed < 0.6)
            {
                Yield(ahead.Position, 4.6, 2.25);
            }
        }
        foreach (var (stop, signal) in car.Path.Stops)
        {
            var phase = (_time + signal.OffsetSeconds) % signal.CycleSeconds;
            var gap = car.Path.Wrap(stop - car.Distance);
            if (phase >= signal.GreenSeconds && gap < definition.TrafficLookAhead)
                wanted = M.Min(wanted, M.Max(0, (gap - 2.8) * 0.95));
        }

        return wanted;

        void Yield(Vec3d position, double stopAt, double width)
        {
            var offset = (position - car.Position).WithY(0);
            var forward = offset.Dot(heading);
            var lateral = offset - heading * forward;
            if (forward > 0 && forward < definition.TrafficLookAhead && lateral.LengthSquared < width * width)
                wanted = M.Min(wanted, M.Max(0, (forward - stopAt) * 0.85));
        }
    }

    private sealed class Traveler(int id, int appearance, Track path, double distance, double cruise, int direction, string? modelId)
    {
        public int Id { get; } = id;
        public int Appearance { get; } = appearance;
        public Track Path { get; } = path;
        public double Cruise { get; } = cruise;
        public double Distance { get; private set; } = distance;
        public int Direction { get; set; } = direction;
        public double Speed { get; set; } = cruise;
        public double Panic { get; set; }
        public double Startled { get; set; }
        public double Pause { get; set; }
        public double PickpocketAfter { get; set; }
        public double Traveled { get; private set; }
        public double Steering { get; set; }
        public bool Taken { get; set; }
        public string? ModelId { get; } = modelId;
        public Vec3d Position => Path.Sample(Distance);
        public double Yaw
        {
            get
            {
                var tangent = Forward(1);
                return M.Atan2(-tangent.X, -tangent.Z);
            }
        }

        public Vec3d Forward(double lookAhead)
        {
            var delta = Path.Sample(Distance + Direction * lookAhead) - Position;
            if (delta.LengthSquared < 1e-6)
                delta = Position - Path.Sample(Distance - Direction * lookAhead);
            return delta.WithY(0).Normalized();
        }

        public void Advance(double meters)
        {
            if (meters <= 0) return;
            Traveled += meters;
            var next = Distance + Direction * meters;
            if (Path.Loop)
                Distance = Path.Wrap(next);
            else if (next is > 0 && next < Path.Length)
                Distance = next;
            else
            {
                Distance = M.Clamp(next, 0, Path.Length);
                Direction *= -1;
                if (Panic <= 0) Pause = Path.PauseSeconds;
            }
        }
    }

    private sealed class DisplacedDriver(int id, int appearance, Vec3d position, Vec3d direction, double remaining, StaticWorld level)
    {
        public int Id { get; } = id;
        public int Appearance { get; } = appearance;
        public Vec3d Position { get; set; } = position;
        public Vec3d Direction { get; set; } = direction;
        public double Remaining { get; set; } = remaining;
        public StaticWorld Level { get; } = level;
    }

    private sealed class CargoCache(StreetCargoDef definition)
    {
        public StreetCargoDef Definition { get; } = definition;
        public Vec3d Position { get; } = StreetPopulationDef.ToPoint(definition.Position);
        public double ReadyAt { get; set; }
    }

    private sealed class Track
    {
        private readonly Vec3d[] _waypoints;
        private readonly double[] _ends;

        public Track(StreetRouteDef definition)
        {
            Loop = definition.Loop;
            PauseSeconds = definition.PauseSeconds;
            var points = definition.Points.Select(StreetPopulationDef.ToPoint).ToArray();
            _waypoints = Loop ? [.. points, points[0]] : points;
            _ends = new double[_waypoints.Length];
            for (var i = 1; i < _waypoints.Length; i++)
                _ends[i] = _ends[i - 1] + Vec3d.Distance(_waypoints[i - 1], _waypoints[i]);
            Stops = definition.Signals.Select(signal => (_ends[signal.Waypoint], signal)).ToArray();
        }

        public bool Loop { get; }
        public double PauseSeconds { get; }
        public double Length => _ends[^1];
        public (double Distance, StreetSignalDef Signal)[] Stops { get; }

        public double Wrap(double distance) => (distance % Length + Length) % Length;

        public Vec3d Sample(double distance)
        {
            distance = Loop ? Wrap(distance) : M.Clamp(distance, 0, Length);
            var endpoint = Array.BinarySearch(_ends, distance);
            if (endpoint >= 0) return _waypoints[endpoint];
            var next = ~endpoint;
            var segment = (distance - _ends[next - 1]) / (_ends[next] - _ends[next - 1]);
            return Vec3d.Lerp(_waypoints[next - 1], _waypoints[next], segment);
        }
    }
}
