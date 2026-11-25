using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Harbor.Core.Data;
using Harbor.Core.Net;
using Harbor.Core.Ports;
using Harbor.Core.Server;
using Harbor.Core.Vehicles;

const string usage = "Usage: Harbor.Server [--listen IP_ADDRESS] [--port PORT]";
const int maxPlayers = 4;
if (args.Length == 1 && args[0] is "--help" or "-h")
{
    Console.WriteLine(usage);
    Console.WriteLine("Default address: 127.0.0.1, default port: 7788.");
    return 0;
}

var address = IPAddress.Loopback;
var port = 7788;
for (var i = 0; i < args.Length; i += 2)
{
    if (i + 1 < args.Length)
    {
        var value = args[i + 1];
        if (args[i] == "--listen" && IPAddress.TryParse(value, out var parsedAddress))
        {
            address = parsedAddress;
            continue;
        }

        if (args[i] == "--port" && int.TryParse(value, out var parsedPort) && parsedPort is >= 0 and <= 65535)
        {
            port = parsedPort;
            continue;
        }
    }

    Console.Error.WriteLine(usage);
    return 2;
}

using var stop = new CancellationTokenSource();
ConsoleCancelEventHandler onCancel = (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
Console.CancelKeyPress += onCancel;
using var onTerminate = OperatingSystem.IsWindows()
    ? null
    : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        stop.Cancel();
    });

var log = new ConsoleLog();
try
{
    var files = new DiskFileSystem(AppContext.BaseDirectory);
    var combat = GameData.LoadCombat(files);
    var encounter = HarborLockdownDef.Load(files, "game/data/missions/harbor_lockdown.json", combat.Weapons);
    var layout = HarborLayout.Load(files, "data/world/harbor_front.json");
    var vehicle = ArcadeVehicleTuning.Load(files, "data/vehicles/calloway_compact.json");

    using var transport = TcpTransport.Listen(address, port, maxPlayers);
    var authority = new HarborAuthority(combat, encounter, layout, vehicle, transport, log, maxPlayers);
    authority.World.Events.Subscribe<PlayerJoined>(joined => log.Info($"Player {joined.ActorId} joined."));
    authority.World.Events.Subscribe<PlayerLeft>(left => log.Info($"Player {left.ActorId} left."));

    log.Info($"Project Harbor listening on {transport.LocalEndPoint} (TCP, {authority.Session.Config.MaxPlayers} players).");
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(authority.World.Loop.StepSeconds / 2));
    var previous = Stopwatch.GetTimestamp();
    while (await timer.WaitForNextTickAsync(stop.Token))
    {
        var now = Stopwatch.GetTimestamp();
        transport.Pump();
        authority.Update(Stopwatch.GetElapsedTime(previous, now).TotalSeconds);
        previous = now;
    }

    return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    log.Info("Project Harbor stopped.");
    return 0;
}
catch (Exception error) when (error is IOException or SocketException or FormatException or InvalidOperationException or DataException)
{
    log.Error(error.Message);
    return 1;
}
finally
{
    Console.CancelKeyPress -= onCancel;
}

internal sealed class ConsoleLog : ILog
{
    public void Info(string message) => Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] {message}");
    public void Warn(string message) => Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:O}] Warning: {message}");
    public void Error(string message) => Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:O}] Error: {message}");
}
