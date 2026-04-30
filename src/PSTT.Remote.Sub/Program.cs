using PSTT.Remote;
using Spectre.Console;
using System.Collections.Concurrent;

// ── Argument parsing ──────────────────────────────────────────────────────────

var host     = "localhost";
var port     = 0;
var topics   = new List<string>();
var showTs   = false;
var treeMode = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host":      host     = args[++i]; break;
        case "--port":      port     = int.Parse(args[++i]); break;
        case "--topic":     topics.Add(args[++i]); break;
        case "--timestamp": showTs   = true; break;
        case "--tree":      treeMode = true; break;
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
    .WithAutoReconnect()
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

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── Subscribe & display ───────────────────────────────────────────────────────

if (treeMode)
{
    Console.Error.WriteLine("Tree mode — press Ctrl+C to exit.");

    var topicState = new ConcurrentDictionary<string, (string? Value, bool IsActive, string? StatusMsg)>();

    foreach (var topic in topics)
    {
        cache.Subscribe(topic, async sub =>
        {
            topicState[sub.Key] = (
                sub.Value,
                sub.Status.IsActive,
                sub.Status.IsActive ? null : $"{sub.Status.State}: {sub.Status.Message}"
            );
            await Task.CompletedTask;
        });
    }

    await AnsiConsole.Live(BuildTopicTree(host, port, topicState))
        .AutoClear(false)
        .Overflow(VerticalOverflow.Ellipsis)
        .Cropping(VerticalOverflowCropping.Bottom)
        .StartAsync(async ctx =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                ctx.UpdateTarget(BuildTopicTree(host, port, topicState));
                try { await Task.Delay(200, cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
}
else
{
    Console.Error.WriteLine("Press Ctrl+C to exit.");

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

    try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (TaskCanceledException) { }
}

Console.Error.WriteLine("Disconnected.");
return 0;

// ── Helpers ───────────────────────────────────────────────────────────────────

static Tree BuildTopicTree(
    string host, int port,
    ConcurrentDictionary<string, (string? Value, bool IsActive, string? StatusMsg)> state)
{
    var tree = new Tree($"[bold blue]{host}:{port}[/]");

    var snapshot = state.ToArray()
        .OrderBy(kv => kv.Key)
        .ToArray();

    // Map of topic path prefix → its Spectre TreeNode
    var nodes = new Dictionary<string, TreeNode>();

    foreach (var (key, (value, isActive, statusMsg)) in snapshot)
    {
        var segments = key.Split('/');
        string prefix = "";
        TreeNode? parentNode = null;

        for (int i = 0; i < segments.Length; i++)
        {
            prefix = i == 0 ? segments[i] : $"{prefix}/{segments[i]}";
            bool isLeaf = i == segments.Length - 1;

            if (!nodes.TryGetValue(prefix, out var node))
            {
                string label = isLeaf
                    ? FormatLeaf(segments[i], value, isActive, statusMsg)
                    : $"[yellow]{Markup.Escape(segments[i])}[/]/";

                node = parentNode is null
                    ? tree.AddNode(label)
                    : parentNode.AddNode(label);

                nodes[prefix] = node;
            }

            parentNode = nodes[prefix];
        }
    }

    if (snapshot.Length == 0)
        tree.AddNode("[dim]Waiting for data…[/]");

    return tree;
}

static string FormatLeaf(string segment, string? value, bool isActive, string? statusMsg) =>
    isActive
        ? $"[cyan]{Markup.Escape(segment)}[/] = [green]{Markup.Escape(value ?? "")}"
        : $"[dim]{Markup.Escape(segment)} [{Markup.Escape(statusMsg ?? "stale")}][/]";

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
          --timestamp       Prefix each line with a UTC timestamp (streaming mode only)
          --tree            Live tree view grouped by topic path segments
          --help            Show this help

        Output format (streaming, default):
          topic/path=value
          topic/path [Stale: Remote server disconnected]

        Output format (--tree):
          Live-updating ANSI tree refreshed every 200 ms.
          Not suitable for piping — use streaming mode for that.

        Examples:
          pstt-sub --port 5010 --topic sensors/#
          pstt-sub --host 192.168.1.10 --port 5010 --topic home/+ --topic $DASHBOARD/#
          pstt-sub --port 5010 --topic sensors/# --tree
        """);
}
