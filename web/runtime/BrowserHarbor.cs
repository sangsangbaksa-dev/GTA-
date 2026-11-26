using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Harbor.Core.Combat;
using Harbor.Core.Data;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Ports;
using Harbor.Core.Server;
using Harbor.Core.Sim;
using Harbor.Core.Vehicles;
using Harbor.Core.World;

namespace Harbor.Browser;

[SupportedOSPlatform("browser")]
public static partial class BrowserHarbor
{
    private static readonly JsonSerializerOptions WebJson = new(Json.Options)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IgnoreReadOnlyProperties = true,
    };
    private static readonly EmbeddedFileSystem Files = new(typeof(BrowserHarbor).Assembly);
    private static readonly Lazy<HarborLayout> Layout = new(() => HarborLayout.Load(Files, "data/world/harbor_front.json"));
    private static readonly Lazy<GameData> Data = new(() => GameData.LoadCombat(Files));
    private static readonly Lazy<HarborLockdownDef> Encounter = new(() =>
        HarborLockdownDef.Load(Files, "data/missions/harbor_lockdown.json", Data.Value.Weapons));
    private static readonly Lazy<StreetPopulationDef> Population = new(() =>
        StreetPopulationDef.Load(Files, "data/world/harbor_population.json"));
    private static readonly Lazy<ArcadeVehicleTuning> Tuning = new(() =>
        ArcadeVehicleTuning.Load(Files, "data/vehicles/calloway_compact.json"));
    private static readonly Lazy<VehicleCatalog> Vehicles = new(() => VehicleCatalog.Load(Files, "data/vehicles", Tuning.Value));
    private static readonly List<ShotFired> Shots = [];
    private static FixedTickLoop _clock = null!;
    private static InMemoryNetwork _network = null!;
    private static HarborAuthority _authority = null!;
    private static ClientSession _client = null!;
    private static InputButtons _presses;
    private static InputButtons _held;
    private static double _right, _forward, _yaw, _pitch;
    private static byte _slot;

    public static void Main() { }

    [JSExport]
    public static string Start()
    {
        _clock = new FixedTickLoop();
        _network = new InMemoryNetwork { LatencySeconds = 0 };
        var endpoint = _network.CreateServer();
        _authority = new HarborAuthority(Data.Value, Encounter.Value, Layout.Value, Tuning.Value, endpoint,
            new BrowserLog(), ambient: Population.Value, vehicles: Vehicles.Value, sandbox: true);
        _client = new ClientSession(_network.Connect(endpoint), "Rook", staticWorld: _authority.Level, interpolationDelayTicks: 0);
        _presses = _held = InputButtons.None;
        _right = _forward = _yaw = _pitch = 0;
        _slot = 0;
        Shots.Clear();
        for (var attempt = 0; attempt < 6 && !_client.IsJoined; attempt++) Advance(_clock.StepSeconds);
        if (!_client.IsJoined) throw new InvalidOperationException("The local authority did not accept the player.");
        return ReadFrame();
    }

    [JSExport]
    public static string Tick(double elapsed, double right, double forward, double yaw, double pitch,
        int held, int pressed, int weaponSlot)
    {
        _right = right;
        _forward = forward;
        _yaw = yaw;
        _pitch = pitch;
        _held = (InputButtons)held;
        _presses |= (InputButtons)pressed;
        _slot = (byte)weaponSlot;
        var steps = _clock.Advance(System.Math.Clamp(elapsed, 0, 0.12), Advance);
        return steps > 0 ? ReadFrame() : "";
    }

    [JSExport]
    public static double CameraDistance(double x, double y, double z, double dx, double dy, double dz, double distance)
    {
        var hit = _authority.Level.Raycast(new Vec3d(x, y, z), new Vec3d(dx, dy, dz), distance, out var obstacle);
        return hit ? System.Math.Max(0.12, obstacle.Distance - 0.22) : distance;
    }

    private static void Advance(double seconds)
    {
        _network.Advance(seconds);
        _client.Poll();
        if (_client.IsJoined)
        {
            _client.Tick(_right, _forward, _yaw, _pitch, _held | _presses, _slot);
            _presses = InputButtons.None;
        }
        _network.Advance(0);
        _authority.Update(seconds);
        _network.Advance(0);
        _client.Poll();
        while (_client.Events.TryDequeue(out var message))
            if (message.Type == HarborProtocol.Shot && HarborProtocol.Decode<ShotFired>(message.Payload) is { } shot)
                Shots.Add(shot);
    }

    private static string ReadFrame()
    {
        var actor = _authority.World.Actors.Get(_client.LocalActorId);
        var state = _authority.GetState(actor.Id);
        var move = actor.Move;
        var json = JsonSerializer.Serialize(new BrowserFrame(state, actor.Id, move.Position, move.Velocity, move.Yaw,
            move.Crouched, move.Aiming, move.Sprinting, actor.IsAlive, Shots), WebJson);
        Shots.Clear();
        return json;
    }

    private sealed record BrowserFrame(HarborState State, int ActorId, Vec3d Position, Vec3d Velocity, double Yaw,
        bool Crouched, bool Aiming, bool Sprinting, bool Alive, IReadOnlyList<ShotFired> Shots);

    private sealed class BrowserLog : ILog
    {
        public void Info(string message) => Console.WriteLine(message);
        public void Warn(string message) => Console.Error.WriteLine(message);
        public void Error(string message) => Console.Error.WriteLine(message);
    }
}
