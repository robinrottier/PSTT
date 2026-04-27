using PSTT.Remote;

// ── Argument parsing ──────────────────────────────────────────────────────────

var host    = "localhost";
var port    = 0;
var topics  = new List<string>();
var showTs  = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host":      host    = args[++i]; break;
        case "--port":      port    = int.Parse(args[++i]); break;
        case "--topic":     topics.Add(args[++i]); break;
        case "--timestamp": showTs  = true; break;
        case "--help": case "-h": PrintUsage(); return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            PrintUsage();
            return 1;
    }
}

if (port <= 0 || topics.Count == 0) { PrintUsage(); return 1; }

// ── Connect ───────────────────────────────────────────────────────────────────

Console.Error.WriteLine($"Connecting to {host}:{port} ...");

await using var cache = new RemoteCacheBuilder<string>()
    .WithTcpTransport(host, port)
    .WithUtf8Encoding()
    .Build();

try
{
    await cache.ConnectAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Connection failed: {ex.Message}");
    return 1;
}

Console.Error.WriteLine($"Connected. Subscribing to: {string.Join(", ", topics)}");
Console.Error.WriteLine("Press Ctrl+C to exit.");

// ── Subscribe ─────────────────────────────────────────────────────────────────

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

foreach (var topic in topics)
{
    cache.Subscribe(topic, async sub =>
    {
        var prefix = showTs ? $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} " : "";
        var line = sub.Status.IsActive
            ? $"{prefix}{sub.Key}={sub.Value}"
            : $"{prefix}{sub.Key} [{sub.Status.State}: {sub.Status.Message}]";

        Console.WriteLine(line);
        await Task.CompletedTask;
    });
}

// Wait until Ctrl+C
try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (TaskCanceledException) { }

Console.Error.WriteLine("Disconnected.");
return 0;

// ── Helpers ───────────────────────────────────────────────────────────────────

static void PrintUsage()
{
    Console.Error.WriteLine("""
        pstt-sub - Subscribe to topics on a PSTT TCP cache server

        Usage:
          pstt-sub --port <port> --topic <topic> [options]

        Required:
          --port <port>     TCP port of the cache server
          --topic <topic>   Topic or wildcard pattern to subscribe to (repeatable)

        Options:
          --host <host>     Server hostname (default: localhost)
          --timestamp       Prefix each line with a UTC timestamp
          --help            Show this help

        Output format (stdout):
          topic/path=value
          topic/path [Stale: Remote server disconnected]

        Examples:
          pstt-sub --port 5010 --topic sensors/#
          pstt-sub --host 192.168.1.10 --port 5010 --topic home/+ --topic $DASHBOARD/#
        """);
}
