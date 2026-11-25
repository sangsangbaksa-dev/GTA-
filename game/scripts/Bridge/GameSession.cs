using System;
using System.Collections.Generic;
using Godot;
using Harbor.Core.Client;
using Harbor.Core.Data;
using Harbor.Core.Geometry;
using Harbor.Core.Net;
using Harbor.Core.Ports;
using Harbor.Core.Server;
using Harbor.Core.Sim;
using Harbor.Game.Adapters;
using Harbor.Game.Player;
using Harbor.Game.UI;
using Harbor.Game.Views;

namespace Harbor.Game.Bridge;

/// <summary>Root of a running game (ADR 0008). Hosts the engine-agnostic <see cref="GameServer"/> (offline, listen
/// server or headless dedicated) and/or a <see cref="ClientSession"/>, steps them, and hands their state to the
/// views. Godot never decides game rules: it only sends inputs/requests and draws what the core says.</summary>
public partial class GameSession : Node3D
{
    public enum Mode { Offline, Host, Join, Dedicated }

    public const int DefaultPort = 27960;

    public GameData Data { get; private set; } = null!;
    public GameServer? Server { get; private set; }
    public ClientSession? Client { get; private set; }
    public ClientGameState State { get; } = new();
    /// <summary>Client copy of level collision: used for movement prediction, camera and aim traces.</summary>
    public StaticWorld ClientStatic { get; } = new();
    /// <summary>Placement index → static box id (same ids on server and client because both build from map.json in order).</summary>
    public IReadOnlyDictionary<int, int> PlacementBoxes => _placementBoxes;
    public Mode CurrentMode { get; private set; }
    public bool Running { get; private set; }
    /// <summary>Fraction of the current client tick that has elapsed (render interpolation).</summary>
    public double Alpha { get; private set; }
    public MoveState PrevPredicted { get; private set; }
    public MoveState Predicted { get; private set; }
    public string PlayerName { get; private set; } = "rook";

    public event Action? Started;
    public event Action<ClientFx>? FxRaised;

    private Dictionary<int, int> _placementBoxes = new();
    private InMemoryNetwork? _memNet;
    private ENetTransport? _serverTransport, _clientTransport;
    private double _clientAccum;
    private double _smokeSeconds = -1, _smokeElapsed;
    private string? _screenshotPath;
    private double _screenshotAt = 5;
    private Harbor.Core.Math.Vec3d? _devSpawn;
    private bool _devSpawnDone;
    private Dictionary<string, string> _devArgs = new();
    private PlayerInput? _input;
    private WorldBuilder? _world;
    private ActorViews? _actors;
    private Hud? _hud;
    private Phone? _phone;
    private MainMenu? _menu;
    private ulong _nonce = (ulong)DateTime.UtcNow.Ticks;

    public override void _Ready()
    {
        var log = new GodotLog();
        Data = GameData.Load(DataPaths.FileSystem(), "data");
        Locale.Load(DataPaths.FileSystem());
        log.Info($"data loaded: {Data.Weapons.Count} weapons, {Data.Map.Placements.Count} placements, {Data.Agents.Encounters.Count} encounters");
        _placementBoxes = Data.Map.BuildStatic(ClientStatic);

        var args = ParseArgs(OS.GetCmdlineUserArgs());
        if (args.TryGetValue("smoke-test", out var smoke)) _smokeSeconds = double.Parse(smoke, System.Globalization.CultureInfo.InvariantCulture);
        if (args.TryGetValue("screenshot", out var shot)) _screenshotPath = shot;
        _devArgs = args;
        if (args.TryGetValue("shot-at", out var sat)) _screenshotAt = double.Parse(sat, System.Globalization.CultureInfo.InvariantCulture);
        if (args.TryGetValue("spawn", out var sp))
        {
            var xz = sp.Split(',');
            _devSpawn = new Harbor.Core.Math.Vec3d(double.Parse(xz[0], System.Globalization.CultureInfo.InvariantCulture), 0,
                double.Parse(xz[1], System.Globalization.CultureInfo.InvariantCulture));
        }
        var port = args.TryGetValue("port", out var ps) ? int.Parse(ps) : DefaultPort;
        var name = args.GetValueOrDefault("name", "rook");

        if (args.ContainsKey("server") || DisplayServer.GetName() == "headless" && !args.ContainsKey("offline") && !args.ContainsKey("join"))
        {
            Start(Mode.Dedicated, name, "", port);
            return;
        }
        BuildPresentation();
        if (args.ContainsKey("offline")) Start(Mode.Offline, name, "", port);
        else if (args.ContainsKey("host")) Start(Mode.Host, name, "", port);
        else if (args.TryGetValue("join", out var addr)) Start(Mode.Join, name, addr, port);
        else
        {
            _menu = new MainMenu(this);
            AddChild(_menu);
        }
    }

    private void BuildPresentation()
    {
        _world = new WorldBuilder(this) { Name = "World" };
        AddChild(_world);
        _actors = new ActorViews(this) { Name = "Actors" };
        AddChild(_actors);
        _input = new PlayerInput(this) { Name = "PlayerInput" };
        if (_devArgs.TryGetValue("orbit", out var orbit)) _input.DevOrbit = Mathf.DegToRad(float.Parse(orbit, System.Globalization.CultureInfo.InvariantCulture));
        if (_devArgs.TryGetValue("hold", out var hold))
            foreach (var h in hold.Split(','))
                if (Enum.TryParse<InputButtons>(h, true, out var btn)) _input.DevHold |= btn;
        if (_devArgs.TryGetValue("cam-dist", out var cd)) _input.DevDistance = float.Parse(cd, System.Globalization.CultureInfo.InvariantCulture);
        AddChild(_input);
        _hud = new Hud(this) { Name = "Hud" };
        AddChild(_hud);
        _phone = new Phone(this) { Name = "Phone" };
        AddChild(_phone);
    }

    public void Start(Mode mode, string playerName, string address, int port)
    {
        if (Running) return;
        CurrentMode = mode;
        PlayerName = string.IsNullOrWhiteSpace(playerName) ? "rook" : playerName.Trim();
        var log = new GodotLog();
        switch (mode)
        {
            case Mode.Offline:
                _memNet = new InMemoryNetwork { LatencySeconds = 0 };
                var ep = _memNet.CreateServer();
                Server = new GameServer(Data, ep, log);
                Client = new ClientSession(_memNet.Connect(ep), PlayerName, Server.Session.MovementConfig, ClientStatic);
                break;
            case Mode.Host:
                _serverTransport = ENetTransport.Listen(port, 16);
                Server = new GameServer(Data, _serverTransport, log);
                _clientTransport = ENetTransport.Connect("127.0.0.1", port);
                Client = new ClientSession(_clientTransport, PlayerName, null, ClientStatic);
                break;
            case Mode.Join:
                _clientTransport = ENetTransport.Connect(address, port);
                Client = new ClientSession(_clientTransport, PlayerName, null, ClientStatic);
                break;
            case Mode.Dedicated:
                _serverTransport = ENetTransport.Listen(port, 16);
                Server = new GameServer(Data, _serverTransport, log);
                log.Info($"dedicated server listening on UDP {port} (max 16 players)");
                break;
        }
        Running = true;
        _menu?.QueueFree();
        _menu = null;
        Started?.Invoke();
    }

    public override void _Process(double delta)
    {
        if (!Running) return;
        _smokeElapsed += delta;
        if (_devSpawn is { } spawn && !_devSpawnDone && Server != null && Client is { IsJoined: true }
            && Server.World.Actors.TryGet(Client.LocalActorId, out var meActor))
        {
            // developer convenience (--spawn x,z): start somewhere specific when hosting
            meActor.Position = spawn;
            _devSpawnDone = true;
        }
        if (_screenshotPath != null && _smokeElapsed >= _screenshotAt)
        {
            GetViewport().GetTexture().GetImage().SavePng(_screenshotPath);
            GD.Print($"[Harbor] screenshot saved to {_screenshotPath}");
            _screenshotPath = null;
        }
        if (_smokeSeconds > 0)
        {
            if (_smokeElapsed >= _smokeSeconds) { SmokeReport(); GetTree().Quit(); return; }
        }
        _memNet?.Advance(delta);
        Server?.Update(delta);
        if (Client == null) return;

        var step = 1.0 / Client.TickRate;
        _clientAccum += delta;
        var steps = 0;
        while (_clientAccum >= step && steps++ < 5)
        {
            _clientAccum -= step;
            Client.Poll();
            while (Client.Events.TryDequeue(out var e)) State.Apply(e.Type, e.Payload);
            while (State.Fx.TryDequeue(out var fx)) OnFx(fx);
            var cmd = _input?.Sample() ?? default;
            PrevPredicted = Predicted;
            Client.Tick(cmd.Right, cmd.Forward, cmd.Yaw, cmd.Pitch, cmd.Buttons, cmd.Slot);
            Predicted = Client.Predicted;
            if (PrevPredicted.Position.LengthSquared == 0) PrevPredicted = Predicted;
        }
        if (_clientAccum > step) _clientAccum = 0;
        Alpha = _clientAccum / step;
    }

    private void OnFx(ClientFx fx)
    {
        if (fx is ExplosionFx ex) ClientStatic.Remove(ex.BoxId);
        if (fx is TeleportFx) PrevPredicted = Predicted = Client!.Predicted;
        FxRaised?.Invoke(fx);
    }

    /// <summary>Sends a reliable request (buy, launder, start contract...). Returns the nonce.</summary>
    public ulong Request(ushort type, string a = "", string b = "", long x = 0, long y = 0)
    {
        var nonce = ++_nonce;
        Client?.SendRequest(type, new RequestBody(nonce, a, b, x, y).Encode());
        return nonce;
    }

    public int LocalActorId => Client?.LocalActorId ?? -1;

    public override void _ExitTree()
    {
        _clientTransport?.Close();
        _serverTransport?.Close();
    }

    private void SmokeReport()
    {
        var actors = Server?.World.Actors.Count ?? 0;
        var npcs = Server?.Agents.Agents.Count ?? 0;
        GD.Print($"[Harbor] SMOKE OK mode={CurrentMode} joined={Client?.IsJoined} actors={actors} npcs={npcs} " +
                 $"wallet={State.Wallet} bank={State.Bank} pos={Predicted.Position} views={_actors?.ViewCount ?? 0}");
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var d = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            var eq = key.IndexOf('=');
            if (eq >= 0) { d[key[..eq]] = key[(eq + 1)..]; continue; }
            d[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
        }
        return d;
    }
}
