using Harbor.Core.Combat;
using Harbor.Core.Data;
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
}

public sealed record StreetPopulationDef
{
    public const int MaxPedestrians = 24;
    public const int MaxTraffic = 8;
    public int Seed { get; init; } = 7219;
    public double VisibilityRadius { get; init; } = 140;
    public double HearingRadius { get; init; } = 23;
    public double FleeSeconds { get; init; } = 5;
    public double FleeSpeed { get; init; } = 3.9;
    public StreetRouteDef[] PedestrianRoutes { get; init; } = [];
    public StreetRouteDef[] TrafficRoutes { get; init; } = [];

    public static StreetPopulationDef Load(IFileSystem files, string path)
    {
        var def = Json.Read<StreetPopulationDef>(files, path);
        Json.Require(def.VisibilityRadius is > 0 and <= 400 && def.HearingRadius is > 0 and <= 100
            && def.FleeSeconds is > 0 and <= 30 && def.FleeSpeed is > 0 and <= 12, $"{path}: invalid population tuning");
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
                if (point is not { Length: 3 } || point.Any(coordinate => !double.IsFinite(coordinate) || M.Abs(coordinate) > 2000))
                    throw new DataException($"{path}: invalid waypoint ({route.Id})");

            var points = route.Points.Select(ToPoint).ToArray();
            var segments = route.Loop ? points.Length : points.Length - 1;
            for (var i = 0; i < segments; i++)
                if ((points[i] - points[(i + 1) % points.Length]).WithY(0).LengthSquared < 0.25 * 0.25)
                    throw new DataException($"{path}: adjacent waypoints are too close ({route.Id})");
        }
    }

    internal static Vec3d ToPoint(double[] point) => new(point[0], point[1], point[2]);
}

public readonly record struct StreetPedestrianState(int Id, int Appearance, Vec3d Position, double Yaw, double Speed, bool Fleeing);
public readonly record struct StreetTrafficState(int Id, int Appearance, Vec3d Position, double Yaw, double Speed);

public sealed record StreetPopulationState
{
    public StreetPedestrianState[] Pedestrians { get; init; } = [];
    public StreetTrafficState[] Traffic { get; init; } = [];
}

/// <summary>Authoritative ambient pedestrians and road traffic. Routes are baked from the visible map.
/// These agents live separately from combat actors and keep the same identities between snapshots.</summary>
public sealed class StreetPopulation(StreetPopulationDef definition) : ISimSystem
{
    private Traveler[] _pedestrians = [];
    private Traveler[] _traffic = [];
    private SimWorld _world = null!;
    private readonly HashSet<int> _stolenTraffic = new();

    public void Initialize(SimWorld world)
    {
        _world = world;
        var random = new Random(definition.Seed);
        _pedestrians = CreateTravelers(definition.PedestrianRoutes, random, pedestrians: true);
        _traffic = CreateTravelers(definition.TrafficRoutes, random, pedestrians: false);
        world.Events.Subscribe<ShotFired>(HearShot);
    }

    public void Step(double dt)
    {
        foreach (var person in _pedestrians)
        {
            person.Panic = M.Max(0, person.Panic - dt);
            person.Pause = M.Max(0, person.Pause - dt);
            person.Speed = person.Panic > 0 ? definition.FleeSpeed : person.Pause > 0 ? 0 : person.Cruise;
            person.Advance(person.Speed * dt);
        }

        foreach (var car in _traffic)
        {
            if (_stolenTraffic.Contains(car.Id)) continue;
            var wanted = CarTargetSpeed(car);
            var change = wanted > car.Speed ? 2.6 * dt : -6.0 * dt;
            car.Speed = wanted > car.Speed ? M.Min(wanted, car.Speed + change) : M.Max(wanted, car.Speed + change);
            car.Advance(car.Speed * dt);
        }
    }

    public StreetPopulationState GetState(Vec3d? observer = null)
    {
        var radiusSquared = definition.VisibilityRadius * definition.VisibilityRadius;
        bool Visible(Traveler item) => observer is null || (item.Position - observer.Value).WithY(0).LengthSquared <= radiusSquared;

        return new StreetPopulationState
        {
            Pedestrians = _pedestrians.Where(Visible).Select(person =>
                new StreetPedestrianState(person.Id, person.Appearance, Round(person.Position), M.Round(person.Yaw, 3),
                    M.Round(person.Speed, 2), person.Panic > 0)).ToArray(),
            Traffic = _traffic.Where(car => !_stolenTraffic.Contains(car.Id) && Visible(car)).Select(car =>
                new StreetTrafficState(car.Id, car.Appearance, Round(car.Position), M.Round(car.Yaw, 3), M.Round(car.Speed, 2))).ToArray()
        };
    }

    // Ambient actors are route-driven; interactions use their authoritative positions.
    public bool TryStreetCrime(Actor player, bool assault, Func<Vec3d, bool> reachable,
        out int cash, out bool reported, out bool counterattack)
    {
        cash = 0; reported = false; counterattack = false;
        var victim = _pedestrians.Where(p => (p.Position - player.Position).LengthSquared <= 2.2 * 2.2
            && reachable(p.Position)).MinBy(p => (p.Position - player.Position).LengthSquared);
        if (victim == null || (!assault && victim.WalletTaken)) return false;
        if (!assault)
        {
            victim.WalletTaken = true;
            // Seeded game outcome, independent of frame rate.
            reported = victim.Id % 3 == 0 || victim.Panic > 0;
            if (!reported) { cash = 15 + victim.Id % 6 * 10; return true; }
        }
        else reported = true;
        counterattack = victim.Id % 4 == 0;
        React(victim, player.Position);
        foreach (var witness in _pedestrians)
        {
            if (witness == victim || (witness.Position - victim.Position).LengthSquared > 12 * 12
                || !reachable(witness.Position)) continue;
            if (witness.Id % 3 == 1) witness.Pause = System.Math.Max(witness.Pause, 3);
            else React(witness, player.Position);
        }
        return true;
    }

    private void React(Traveler person, Vec3d threat)
    {
        if ((person.Position - threat).WithY(0).Dot(person.Forward(1)) < 0) person.Direction *= -1;
        person.Panic = definition.FleeSeconds;
        person.Pause = 0;
    }

    public bool TryTakeTraffic(Actor player, Func<Vec3d, bool> reachable, out StreetTrafficState state)
    {
        var car = _traffic.Where(c => !_stolenTraffic.Contains(c.Id) && c.Speed <= 2.5
            && (c.Position - player.Position).LengthSquared <= 3.2 * 3.2 && reachable(c.Position))
            .MinBy(c => (c.Position - player.Position).LengthSquared);
        state = default;
        if (car == null) return false;
        _stolenTraffic.Add(car.Id);
        state = new StreetTrafficState(car.Id, car.Appearance, car.Position, car.Yaw, car.Speed);
        foreach (var person in _pedestrians)
            if ((person.Position - car.Position).LengthSquared <= 12 * 12) React(person, player.Position);
        return true;
    }

    public void ResetCrimes()
    {
        _stolenTraffic.Clear();
        foreach (var person in _pedestrians) { person.WalletTaken = false; person.Panic = 0; person.Pause = 0; }
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
                travelers.Add(new Traveler(id, (id - 1) % 4, track, fraction * track.Length, cruise, direction));
            }
        }
        return travelers.ToArray();
    }

    private void HearShot(ShotFired shot)
    {
        var radius = M.Min(shot.NoiseRadius, definition.HearingRadius);
        if (radius <= 0) return;
        foreach (var person in _pedestrians)
        {
            var away = (person.Position - shot.Origin).WithY(0);
            if (away.LengthSquared > radius * radius) continue;
            if (away.Dot(person.Forward(1)) < 0) person.Direction *= -1;
            person.Panic = definition.FleeSeconds;
            person.Pause = 0;
        }
    }

    private double CarTargetSpeed(Traveler car)
    {
        var wanted = car.Cruise;
        var heading = car.Forward(4);
        foreach (var actor in _world.Actors.All)
        {
            if (actor.Kind != ActorKind.Player) continue;
            var offset = (actor.Position - car.Position).WithY(0);
            var forward = offset.Dot(heading);
            var lateral = offset - heading * forward;
            if (forward is > 0 and < 12 && lateral.LengthSquared < 3.5)
                wanted = M.Min(wanted, M.Max(0, (forward - 3.1) * 0.85));
        }

        foreach (var ahead in _traffic)
        {
            if (ahead == car || ahead.Path != car.Path || _stolenTraffic.Contains(ahead.Id)) continue;
            var gap = car.Path.Wrap(ahead.Distance - car.Distance);
            if (gap < 13)
                wanted = M.Min(wanted, M.Max(0, (gap - 5.4) * 0.9));
        }
        return wanted;
    }

    private sealed class Traveler(int id, int appearance, Track path, double distance, double cruise, int direction)
    {
        public int Id { get; } = id;
        public int Appearance { get; } = appearance;
        public Track Path { get; } = path;
        public double Cruise { get; } = cruise;
        public double Distance { get; private set; } = distance;
        public int Direction { get; set; } = direction;
        public double Speed { get; set; } = cruise;
        public double Panic { get; set; }
        public bool WalletTaken { get; set; }
        public double Pause { get; set; }
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
        }

        public bool Loop { get; }
        public double PauseSeconds { get; }
        public double Length => _ends[^1];

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
