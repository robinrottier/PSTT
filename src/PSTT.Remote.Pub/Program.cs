using PSTT.Remote;

// ── Argument parsing ──────────────────────────────────────────────────────────

var host  = "localhost";
var port  = 0;
var topic = string.Empty;
var value = string.Empty;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host":  host  = args[++i]; break;
        case "--port":  port  = int.Parse(args[++i]); break;
        case "--topic": topic = args[++i]; break;
        case "--value": value = args[++i]; break;
        case "--help": case "-h": PrintUsage(); return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            PrintUsage();
            return 1;
    }
}

if (port <= 0 || string.IsNullOrEmpty(topic) || string.IsNullOrEmpty(value))
{
    PrintUsage();
    return 1;
}

// ── Connect and publish ───────────────────────────────────────────────────────

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

try
{
    await cache.PublishAsync(topic, value);
    Console.Error.WriteLine($"Published {topic}={value} to {host}:{port}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Publish failed: {ex.Message}");
    return 1;
}

return 0;

// ── Helpers ───────────────────────────────────────────────────────────────────

static void PrintUsage()
{
    Console.Error.WriteLine("""
        pstt-pub - Publish a value to a topic on a PSTT TCP cache server

        Usage:
          pstt-pub --port <port> --topic <topic> --value <value> [options]

        Required:
          --port <port>     TCP port of the cache server
          --topic <topic>   Topic path to publish to
          --value <value>   Value string to publish

        Options:
          --host <host>     Server hostname (default: localhost)
          --help            Show this help

        Examples:
          pstt-pub --port 5010 --topic sensors/temp --value 22.5
          pstt-pub --host 192.168.1.10 --port 5010 --topic home/light/state --value on
        """);
}
