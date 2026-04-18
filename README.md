# PSTT

"Pub Sub Telemetry Transport" (PSTT) is a high-performance, thread-safe pub/sub data caching and transport library for .NET 10 
that provides an in-memory data cache with subscription-based updates
and connectivity to a network of related caches and 3rd party sources such as MQTT brokers. 

## Features

- **Pub/Sub Pattern** - Subscribe to topics/keys and receive updates via callbacks
- **Thread-Safe** - Fully concurrent operations using lock-free data structures
- **Flexible Dispatchers** - Control how callbacks execute (synchronous, async, GUI thread)
- **Builder Pattern** - Fluent API for easy configuration
- **Wildcard Subscriptions** - MQTT-style `+` and `#` wildcard matching
- **Publisher Handle** - `IPublisher<TKey,TValue>` tracks producer lifetime and auto-publishes Stale on dispose
- **SubscribeAsync** - `await SubscribeAsync(key, callback)` waits for first non-Pending value
- **Pluggable Pattern Matching** - `IWildcardMatcher<TKey>` interface; built-in `MqttPatternMatcher`
- **Upstream Chaining** - Connect to another cache (or any `ICache`) for cache-miss forwarding
- **Status Tracking** - Values have states: Pending, Active, Stale, Failed
- **Retained Values** - Optional value persistence for late subscribers
- **Configurable Limits** - Control topics, subscriptions, and callback concurrency
- **Cancellation Support** - CancellationToken throughout async operations
- **MQTT Adapter** - `PSTT.Mqtt` project provides an `ICache` backed by an MQTT broker
- **Remote Client/Server** - `PSTT.Remote` bridges Cache chains across process or network boundaries over a pluggable transport (TCP, WebSocket, SignalR)

## Projects

| Project | Description |
|---|---|
| `PSTT.Data` | Core library — `Cache<TKey,TValue>` and `CacheWithPatterns<TKey,TValue>` |
| `PSTT.Mqtt` | MQTT broker adapter implementing `ICache<string,TValue>` via MQTTnet |
| `PSTT.Remote` | TCP/WebSocket/SignalR client transport — `RemoteCache<TValue>` and `RemoteCacheServer<TValue>` |
| `PSTT.Remote.AspNetCore` | ASP.NET Core server adapters — SignalR Hub, WebSocket middleware, and DI extensions |

## Quick Start

### Installation

```bash
# Add the project reference to your solution
dotnet add reference path/to/PSTT.Data.csproj
```

### Basic Usage

```csharp
using PSTT.Data;

// Create a simple Cache
var cache = new Cache<string, string>();

// Subscribe to a topic
var subscription = cache.Subscribe("stock/MSFT/price", async s =>
{
Console.WriteLine($"New price: {s.Value} (Status: {s.Status.State})");
});

// Publish a value
await cache.PublishAsync("stock/MSFT/price", "$420.50");

// Unsubscribe when done
cache.Unsubscribe(subscription);
```

## Builder Pattern (Recommended)

The builder pattern provides a clean, fluent API for configuring Caches:

### Simple Configuration

```csharp
var cache = new CacheBuilder<string, int>()
.WithMaxTopics(1000)
.WithMaxSubscriptionsPerTopic(100)
.Build();
```

### High-Performance Configuration

```csharp
// Fire-and-forget callbacks for maximum throughput
var cache = new CacheBuilder<string, double>()
.WithMaxTopics(10000)
.WithThreadPoolCallbacks(waitForCompletion: false)
.WithMaxCallbackConcurrency(20)
.Build();

await cache.PublishAsync("sensor/temperature", 23.5);
```

### GUI Application Configuration (MAUI/WPF)

```csharp
// Ensure callbacks execute on UI thread
var cache = new CacheBuilder<string, string>()
.WithDispatcher(new SynchronizationContextDispatcher(
SynchronizationContext.Current!))
.Build();

cache.Subscribe("ui/message", async s =>
{
// Safe to update UI here - callback runs on UI thread
MessageLabel.Text = s.Value;
});
```

### Synchronous/Deterministic Configuration

```csharp
// Callbacks complete before PublishAsync returns
var cache = new CacheBuilder<string, int>()
.WithSynchronousCallbacks()
.Build();

var value = 0;
cache.Subscribe("counter", async s => { value = s.Value; });

await cache.PublishAsync("counter", 42);
// value is guaranteed to be 42 here
```

### Error Handling Configuration

```csharp
var errors = new ConcurrentBag<Exception>();

var cache = new CacheBuilder<string, string>()
.WithCallbackErrorHandler((ex, message) =>
{
Console.WriteLine($"Error: {message}");
errors.Add(ex);
})
.Build();

cache.Subscribe("risky-topic", async s =>
{
throw new InvalidOperationException("Callback error!");
// Error is caught and passed to handler
});
```

## Dispatcher Options

Cache supports three built-in dispatchers that control how subscriber callbacks execute:

### 1. SynchronousDispatcher (Default with Builder)

```csharp
var cache = new CacheBuilder<string, int>()
.WithSynchronousCallbacks()
.Build();
```

- Callbacks execute synchronously on the publishing thread
- `PublishAsync` waits for all callbacks to complete
- Best for: Testing, deterministic behavior, simple scenarios

### 2. ThreadPoolDispatcher

```csharp
// Fire-and-forget (high throughput)
var cache = new CacheBuilder<string, int>()
.WithThreadPoolCallbacks(waitForCompletion: false)
.Build();

// Wait for completion (deterministic but concurrent)
var cache = new CacheBuilder<string, int>()
.WithThreadPoolCallbacks(waitForCompletion: true)
.Build();
```

- Callbacks execute on ThreadPool
- `waitForCompletion: false` = fire-and-forget (fastest)
- `waitForCompletion: true` = wait for all callbacks (concurrent but deterministic)
- Best for: High-throughput scenarios, async operations

### 3. SynchronizationContextDispatcher

```csharp
var cache = new CacheBuilder<string, string>()
.WithDispatcher(new SynchronizationContextDispatcher(
SynchronizationContext.Current!))
.Build();
```

- Callbacks execute on the captured SynchronizationContext (e.g., UI thread)
- Best for: MAUI, WPF, WinForms applications
- Enables safe UI updates from callbacks

### 4. Custom Dispatcher

Implement `ICallbackDispatcher` for custom behavior:

```csharp
public class CustomDispatcher : ICallbackDispatcher
{
public string Name => "Custom";
public bool WaitsForCompletion => true;

public async Task DispatchAsync(Func<Task> callback, 
CancellationToken cancellationToken = default)
{
await Task.Run(callback, cancellationToken);
}
}

var cache = new CacheBuilder<string, int>()
.WithDispatcher(new CustomDispatcher())
.Build();
```

## Complete Examples

### Example 1: Stock Price Monitor

```csharp
var stockCache = new CacheBuilder<string, decimal>()
.WithMaxTopics(500)
.WithThreadPoolCallbacks(waitForCompletion: false)
.Build();

var msftSub = stockCache.Subscribe("MSFT", async s =>
Console.WriteLine($"MSFT: ${s.Value:F2}"));

await stockCache.PublishAsync("MSFT", 420.50m);
stockCache.Unsubscribe(msftSub);
```

### Example 2: Sensor Data with Retained Values

```csharp
var sensorCache = new Cache<string, double>();

// Publish with retain flag - value persists for late subscribers
await sensorCache.PublishAsync("sensor/temp", 23.5, null, retain: true);

// Late subscriber immediately gets the retained value
sensorCache.Subscribe("sensor/temp", async s =>
Console.WriteLine($"Temperature: {s.Value}°C"));
// Callback fires immediately with 23.5
```

### Example 3: Wildcard Subscriptions

```csharp
var cache = new CacheBuilder<string, string>()
    .WithWildcards()
    .Build();

// Subscribe to wildcard patterns (MQTT-style)
cache.Subscribe("sensors/#", async s =>
{
    // Matches: sensors/temp, sensors/humidity, sensors/co2/level1
    // s.Key is the specific matched key, not the pattern
    Console.WriteLine($"{s.Key}: {s.Value}");
});

cache.Subscribe("sensors/+/temperature", async s =>
    Console.WriteLine($"Temperature: {s.Value}"));

await cache.PublishAsync("sensors/room1/temperature", "22.5C");
await cache.PublishAsync("sensors/room1/humidity", "45%");
```

### Example 4: Upstream Chaining (Cache Miss Forwarding)

When a key is subscribed locally but has no value yet, the subscription is forwarded to the upstream.
Any value published upstream is automatically propagated downstream.

```csharp
// An upstream data source (could be in another process / over the network)
var upstream = new Cache<string, string>();

// Local cache that forwards cache misses upstream
var local = new CacheBuilder<string, string>()
    .WithUpstream(upstream)
    .Build();

// Subscriber on the local cache
local.Subscribe("config/setting", async s =>
    Console.WriteLine($"Setting: {s.Value}"));

// Publish to upstream — local subscriber receives the value
await upstream.PublishAsync("config/setting", "enabled");
```

### Example 5: Wildcards + Upstream (Local Cache backed by Remote Source)

Combine wildcard matching with upstream chaining. This is the recommended pattern when building
a local in-process cache in front of a remote or out-of-process data source.

```csharp
// Upstream/remote cache — also tree-capable so it handles wildcard subscriptions
var remoteCache = new CacheBuilder<string, string>()
    .WithWildcards()
    .Build();

// Local cache: wildcards + upstream forwarding.
// supportsWildcards: true tells the local cache it may forward wildcard subscriptions upstream.
// Only set this when the upstream is known to support wildcard matching.
var localCache = new CacheBuilder<string, string>()
    .WithWildcards()
    .WithUpstream(remoteCache, supportsWildcards: true)
    .Build();

localCache.Subscribe("sensors/#", async s =>
    Console.WriteLine($"[{s.Key}] = {s.Value}"));

// Publishing to the remote cache propagates to the local wildcard subscriber.
// Callbacks may arrive concurrently (expected for a real out-of-process upstream) —
// the library handles this race-free.
await remoteCache.PublishAsync("sensors/temp",     "22C");
await remoteCache.PublishAsync("sensors/humidity", "45%");

// Publishing directly to the local cache also matches the wildcard.
await localCache.PublishAsync("sensors/co2", "400ppm");
```

**Retained value delivery on subscription**:

```csharp
// Pre-populate remote with retained values
await remoteCache.PublishAsync("sensors/temp",     "22C", null, retain: true);
await remoteCache.PublishAsync("sensors/humidity", "45%", null, retain: true);

// New wildcard subscriber immediately receives all matching retained values
localCache.Subscribe("sensors/#", async s =>
    Console.WriteLine($"Initial or update: [{s.Key}] = {s.Value}"));
```

**When NOT to set `supportsWildcards: true`**: if the upstream is a plain `PSTT.Data` (no
wildcard support), leave the flag at its default `false`. Wildcard subscriptions stay local,
while exact-key subscriptions still propagate upstream normally.

### Example 6: MQTT Upstream

Use the `PSTT.Mqtt` adapter to back a local cache with a live MQTT broker.
The recommended approach is to use `MqttCacheBuilder<TValue>` for a clean, fluent configuration:

```csharp
using PSTT.Mqtt;

// UTF-8 strings — most common MQTT use case
var mqtt = new MqttCacheBuilder<string>()
    .WithBroker("mqtt.example.com")     // port 1883 is default
    .WithUtf8Encoding()                 // shortcut for UTF-8 byte ↔ string
    .Build();

await mqtt.ConnectAsync();

// Use MQTT as the upstream for a local wildcard cache
var localCache = new CacheBuilder<string, string>()
    .WithWildcards()
    .WithUpstream(mqtt, supportsWildcards: true)
    .Build();

// Local wildcard subscription — the MQTT topic subscription is created automatically
localCache.Subscribe("home/+/temperature", async s =>
    Console.WriteLine($"[{s.Key}] = {s.Value}"));

// MQTT messages for "home/livingroom/temperature" are delivered to the local subscriber.
// The local cache acts as an in-process buffer with retained values and wildcard fan-out.
```

#### JSON-encoded DTOs with credentials

```csharp
var mqtt = new MqttCacheBuilder<SensorReading>()
    .WithBroker("mqtt.example.com", port: 8883)
    .WithJsonEncoding()                 // System.Text.Json, any serialisable type
    .WithClientId("my-service")
    .WithCredentials("user", "secret")
    .WithQualityOfService(MqttQualityOfServiceLevel.ExactlyOnce)
    .WithCallbackErrorHandler((ex, msg) => logger.LogError(ex, msg))
    .Build();
```

#### Low-level constructor (still supported)

```csharp
// Raw constructor — prefer the builder above
var mqtt = new MqttCache<string>(
    brokerHost: "mqtt.example.com",
    brokerPort: 1883,
    deserializer: bytes => Encoding.UTF8.GetString(bytes),
    serializer: value => Encoding.UTF8.GetBytes(value));
```

## API Reference

### Cache<TKey, TValue> / ICache<TKey, TValue>

#### Subscribe
```csharp
ISubscription<TKey, TValue> Subscribe(
    TKey key, 
    Func<ISubscription<TKey, TValue>, Task> callback)
```

Subscribe to a topic. Returns immediately, callback fires when value is available or changes.

#### SubscribeAsync
```csharp
Task<ISubscription<TKey, TValue>> SubscribeAsync(
    TKey key,
    Func<ISubscription<TKey, TValue>, Task> callback,
    CancellationToken cancellationToken = default)
```

Subscribe and **wait** until the first non-Pending value is received (i.e. the topic is active or has
a cached value). Useful when you need to ensure initial data has arrived before proceeding.

```csharp
// Wait up to 5 s for the sensor to become active
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var sub = await cache.SubscribeAsync("sensors/temp", async s =>
{
    Console.WriteLine($"{s.Key} = {s.Value}");
}, cts.Token);
// sub.Value is now the first received value
```

Cancellation throws `OperationCanceledException` but does **not** unsubscribe — the callback
continues to fire for any future publishes.

#### PublishAsync
```csharp
Task PublishAsync(TKey key, TValue value)
Task PublishAsync(TKey key, TValue value, IStatus? status, bool retain = false)
Task PublishAsync(TKey key, IStatus status)   // status-only; value is unchanged
```

Publish a value to all subscribers of the topic.

#### Unsubscribe
```csharp
void Unsubscribe(ISubscription<TKey, TValue> subscription)
```

Remove a subscription.

#### GetValue / TryGetValue
```csharp
TValue? GetValue(TKey key)
bool TryGetValue(TKey key, out TValue value)
```

Get the current cached value without subscribing.

#### RegisterPublisher
```csharp
IPublisher<TKey, TValue> RegisterPublisher(TKey key, IStatus? disposeStatus = null)
```

Register a named producer for `key`. Returns an `IPublisher<TKey, TValue>` handle.
Multiple callers can register for the same key — a shared reference count is maintained.
When the **last** publisher handle is disposed, `disposeStatus` is automatically published
(default: `Stale`). Useful for tracking producer lifecycle:

```csharp
await using var pub = cache.RegisterPublisher("sensors/temp");

// Publish values while the producer is running
await pub.PublishAsync(23.5);
await pub.PublishAsync(new Status { State = IStatus.StateValue.Stale });

// On dispose: auto-publishes Stale status to all subscribers
```

### CacheBuilder<TKey, TValue>

#### Configuration Methods

```csharp
WithMaxTopics(int max)                     // Limit number of topics
WithMaxSubscriptionsPerTopic(int max)      // Limit subscriptions per topic
WithMaxSubscriptionsTotal(int max)         // Limit total subscriptions
WithMaxCallbackConcurrency(int max)        // Limit concurrent callbacks

WithSynchronousCallbacks()                 // Use SynchronousDispatcher
WithThreadPoolCallbacks(bool wait)         // Use ThreadPoolDispatcher
WithDispatcher(ICallbackDispatcher)        // Custom dispatcher

WithCallbackErrorHandler(Action<Exception, string>)  // Error callback

WithWildcards()                            // Enable MQTT-style wildcard matching (CacheWithPatterns)
WithPatternMatching()                      // Enable wildcards with default MqttPatternMatcher
WithPatternMatching(IWildcardMatcher<TKey>) // Enable wildcards with a custom pattern matcher
WithUpstream(ICache upstream)              // Chain to an upstream cache
                                           //   (exact-key forwarding only)
WithUpstream(ICache upstream,
    supportsWildcards: true)               // Also forward wildcard subscriptions upstream
                                           //   (upstream must support wildcard matching)
```

`WithWildcards()` and `WithPatternMatching()` both produce a `CacheWithPatterns<TKey,TValue>`.
`WithPatternMatching()` additionally stores the matcher in `CacheWithPatterns.PatternMatcher`
for external inspection or custom routing logic.

#### Build
```csharp
ICache<TKey, TValue> Build()
```

Returns a `CacheWithPatterns<TKey,TValue>` when `.WithWildcards()` or `.WithPatternMatching()` was
called, otherwise a plain `Cache<TKey,TValue>`.

### IWildcardMatcher<TKey>

Implement this interface to plug in custom wildcard/pattern syntax:

```csharp
public interface IWildcardMatcher<TKey>
{
    bool IsPattern(TKey key);                  // true if key contains pattern syntax
    bool Matches(TKey pattern, TKey candidate); // true if candidate matches the pattern
}
```

Built-in: `MqttPatternMatcher` — MQTT `+`/`#` with `/`-separated levels.

```csharp
var matcher = new MqttPatternMatcher();
matcher.IsPattern("sensors/+/temp");   // true
matcher.Matches("sensors/#", "sensors/room/temp"); // true
matcher.Matches("a/#", "a");           // true  (# matches parent itself)
```

### MqttCache<TValue> (PSTT.Mqtt)

Use `MqttCacheBuilder<TValue>` (recommended) or the low-level constructor.

#### MqttCacheBuilder<TValue>

```csharp
// ── Broker connection ──────────────────────────────────────────────
WithBroker(string host, int port = 1883)   // Required. Broker address.
WithClientId(string clientId)              // Custom MQTT client ID (default: random GUID)
WithCredentials(string user, string? pass) // Authenticated connections
WithQualityOfService(MqttQualityOfServiceLevel) // Default: AtLeastOnce

// ── Encoding (one of these is required) ───────────────────────────
WithUtf8Encoding()                         // Extension on Builder<string>: UTF-8 byte ↔ string
WithJsonEncoding(JsonSerializerOptions?)   // Any type: System.Text.Json round-trip
WithEncoding(deserializer, serializer?)    // Custom byte[] ↔ TValue functions

// ── Cache config (all optional) ──────────────────────────────
WithMaxTopics(int)
WithMaxSubscriptionsPerTopic(int)
WithMaxSubscriptionsTotal(int)
WithMaxCallbackConcurrency(int)
WithSynchronousCallbacks()
WithThreadPoolCallbacks(bool waitForCompletion)
WithDispatcher(ICallbackDispatcher)
WithCallbackErrorHandler(Action<Exception, string>)

// ── Build ──────────────────────────────────────────────────────────
MqttCache<TValue> Build()             // Throws if broker or encoding not set
```

#### Instance methods

```csharp
await mqtt.ConnectAsync();    // Connect to broker; call before subscribing or publishing
await mqtt.DisconnectAsync(); // Graceful disconnect; subscribed topics go Stale
await mqtt.DisposeAsync();    // Dispose underlying MQTTnet client

// Inherited from ICache<string, TValue>
mqtt.Subscribe("topic/+/pattern", async s => { ... });
await mqtt.PublishAsync("topic/key", value);
mqtt.Unsubscribe(sub);
TValue? v = mqtt.GetValue("topic/key");
```

Supports `+` and `#` wildcard matching on the local side. MQTT broker subscriptions are
ref-counted — only one broker subscription is created per distinct topic pattern.

---

## Remote Client/Server (PSTT.Remote)

`PSTT.Remote` lets a Cache chain span **process or network boundaries** using a
pluggable transport. A `RemoteCache<TValue>` behaves identically to any other upstream
`ICache`; a `RemoteCacheServer<TValue>` wraps any local `ICache` and serves it to
remote clients.

```
[Client Process]
  └── CacheWithPatterns<string,TValue>  (local cache / hot path)
        └── RemoteCache<TValue>
              ↕  TCP / WebSocket / SignalR / custom
        RemoteCacheServer<TValue>
              └── ICache<string, TValue>  (in-process: Cache, MqttCache, …)
[Server Process / ASP.NET App]
```

### Quick Example

**Server side** (standalone process or inside an ASP.NET host):

```csharp
using PSTT.Remote;
using PSTT.Remote.Transport.Tcp;

// The upstream data source this server will proxy
var upstream = new CacheWithPatterns<string, string>();

var server = new RemoteCacheServer<string>(
    upstream,
    serializer:   s => Encoding.UTF8.GetBytes(s),
    deserializer: b => Encoding.UTF8.GetString(b),
    new TcpServerTransport(port: 5000),
    forwardPublish: true);   // allow clients to publish back upstream

await server.StartAsync();
```

**Client side** (separate process):

```csharp
using PSTT.Remote;
using PSTT.Data;

// Use builder (recommended)
var client = new RemoteCacheBuilder<string>()
    .WithTcpTransport("server.example.com", 5000)
    .WithUtf8Encoding()           // or WithJsonEncoding() / WithEncoding(…)
    .Build();

await client.ConnectAsync();

// Subscribe directly to the remote client
client.Subscribe("sensors/+", async s =>
    Console.WriteLine($"{s.Key} = {s.Value}"));

// Or wire it as upstream for a local Cache
var local = new CacheBuilder<string, string>()
    .WithWildcards()
    .WithUpstream(client, supportsWildcards: true)
    .Build();
```

### Wire Protocol

Messages are **length-framed UTF-8 JSON** (4-byte little-endian prefix). Each message is compact:

```json
{ "t": "sub",  "k": "sensors/temp" }
{ "t": "upd",  "k": "sensors/temp", "p": "MjIuNQ==", "ss": 1 }
{ "t": "pub",  "k": "sensors/temp", "p": "MjMuMA==", "r": false }
{ "t": "unsub","k": "sensors/temp" }
```

`p` is base64-encoded TValue bytes. `ss` is the `IStatus.StateValue` integer (0=Pending, 1=Active, 2=Stale, 3=Failed).

### Builder Reference (`RemoteCacheBuilder<TValue>`)

```csharp
// ── Transport (one required) ───────────────────────────────────────
WithTcpTransport(string host, int port)      // Connect via TCP
WithTransport(IRemoteTransport transport)    // Any custom transport

// ── Encoding (one required) ────────────────────────────────────────
WithUtf8Encoding()                           // Extension on Builder<string>: UTF-8 ↔ string
WithJsonEncoding(JsonSerializerOptions?)     // Any type: System.Text.Json round-trip
WithRawEncoding()                            // Extension on Builder<byte[]>: pass-through
WithEncoding(deserializer, serializer?)      // Custom byte[] ↔ TValue

// ── Cache config (all optional) ──────────────────────────────
WithMaxTopics(int)
WithMaxSubscriptionsPerTopic(int)
WithMaxSubscriptionsTotal(int)
WithMaxCallbackConcurrency(int)
WithSynchronousCallbacks()
WithThreadPoolCallbacks(bool waitForCompletion)
WithDispatcher(ICallbackDispatcher)
WithCallbackErrorHandler(Action<Exception, string>)

// ── Build ──────────────────────────────────────────────────────────
RemoteCache<TValue> Build()       // Throws if transport or encoding not set
```

### Transport Abstraction

Two interfaces let you plug in any transport:

```csharp
// Per-connection bidirectional channel
public interface IRemoteTransport : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct = default);
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    event Func<ReadOnlyMemory<byte>, Task>? MessageReceived;
    event Func<Task>? Disconnected;
    bool IsConnected { get; }
}

// Server-side listener that fires ClientConnected per incoming connection
public interface IRemoteServerTransport : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
    event Func<IRemoteTransport, Task>? ClientConnected;
}
```

Bundled implementations: `TcpClientTransport` / `TcpServerTransport` (4-byte LE framing).
`TcpServerTransport(port: 0)` lets the OS assign a free port; read it back via `BoundPort`.

### WebSocket Transport

`WebSocketClientTransport` and `WebSocketServerTransport` (standalone `HttpListener`) are included in `PSTT.Remote`:

```csharp
// ── Server (standalone, non-ASP.NET) ──────────────────────────────────────
var server = new RemoteCacheServer<string>(
    upstream,
    s => Encoding.UTF8.GetBytes(s),
    b => Encoding.UTF8.GetString(b),
    new WebSocketServerTransport("http://localhost:5001/Cache/"),
    forwardPublish: true);

await server.StartAsync();

// ── Client ─────────────────────────────────────────────────────────────────
var client = new RemoteCacheBuilder<string>()
    .WithWebSocketTransport("ws://server.example.com:5001/Cache/")
    .WithUtf8Encoding()
    .Build();

await client.ConnectAsync();
```

### SignalR Transport (client-side)

`SignalRClientTransport` wraps a `HubConnection`. Works in **Blazor WASM** (no raw TCP available in browser):

```csharp
var client = new RemoteCacheBuilder<string>()
    .WithSignalRTransport("https://server.example.com/Cache")
    .WithUtf8Encoding()
    .Build();

await client.ConnectAsync();
```

Or pass a pre-built `HubConnection` (useful in Blazor where you may want to reuse the hub):

```csharp
var hub = new HubConnectionBuilder()
    .WithUrl("https://server.example.com/Cache")
    .Build();

var client = new RemoteCacheBuilder<string>()
    .WithSignalRTransport(hub)
    .WithUtf8Encoding()
    .Build();
```

### ASP.NET Core Server (`PSTT.Remote.AspNetCore`)

For ASP.NET Core hosts (including Blazor Server and APIs), add the `PSTT.Remote.AspNetCore` project:

```csharp
// Program.cs
var upstream = new CacheWithPatterns<string, string>();

// ── SignalR server (Blazor WASM → ASP.NET pattern) ────────────────────────
builder.Services.AddCacheSignalRServer<string>(
    upstream,
    serializer:   s => Encoding.UTF8.GetBytes(s),
    deserializer: b => Encoding.UTF8.GetString(b),
    forwardPublish: true);

builder.Services.AddSignalR();

// ── WebSocket server (plain WebSocket via ASP.NET Core middleware) ─────────
builder.Services.AddCacheWebSocketServer<string>(
    upstream,
    serializer:   s => Encoding.UTF8.GetBytes(s),
    deserializer: b => Encoding.UTF8.GetString(b),
    forwardPublish: true);

var app = builder.Build();

app.UseWebSockets();                         // required for WebSocket support
app.MapCacheHub("/Cache");         // SignalR endpoint
app.MapCacheWebSocket("/dsws");         // WebSocket endpoint
```

**Blazor WASM client** (browser → ASP.NET SignalR):

```csharp
// In Program.cs of the Blazor WASM project:
var client = new RemoteCacheBuilder<string>()
    .WithSignalRTransport("https://my-aspnet-app.example.com/Cache")
    .WithUtf8Encoding()
    .Build();

await client.ConnectAsync();
```

#### Builder extension methods (transport shortcuts)

```csharp
// TCP
WithTcpTransport(string host, int port)
// WebSocket
WithWebSocketTransport(Uri uri)
WithWebSocketTransport(string uri)
// SignalR
WithSignalRTransport(string url)
WithSignalRTransport(HubConnection hub)
// Custom
WithTransport(IRemoteTransport transport)
```



### Lifecycle Behaviour

| Event | Effect |
|---|---|
| `ConnectAsync()` before `Subscribe` | Topics queued; sent after connect |
| `Subscribe` after `ConnectAsync` | `sub` sent immediately |
| Remote disconnect | All subscribed topics marked **Stale** |
| `DisconnectAsync()` / `DisposeAsync()` | Clean teardown; subs cleaned up on server |
| Multiple clients | Each gets independent subscription tracking |

### Wildcard Subscriptions

Use `.WithWildcards()` in the builder for MQTT-style wildcard matching:

- `+` matches a single level: `sensors/+/temp` matches `sensors/room1/temp`
- `#` matches multiple levels: `sensors/#` matches `sensors/room1/temp/max`

The subscription callback receives the **specific matched key** in `s.Key` (e.g. `"sensors/room1/temp"`),
not the wildcard pattern.

### Upstream Chaining

Upstream is wired via the builder — it is **not** a constructor argument or config property.
Any `ICache<TKey,TValue>` can act as an upstream, including `MqttCache<TValue>`.

```csharp
var local = new CacheBuilder<string, int>()
    .WithUpstream(someRemoteSource)
    .Build();
```

### Concurrency Control

```csharp
var cache = new CacheBuilder<string, int>()
.WithMaxCallbackConcurrency(5)  // Max 5 concurrent callbacks per topic
.Build();
```

Limits how many callbacks can run simultaneously. Excess callbacks are skipped.

### CancellationToken Support

All async methods accept `CancellationToken`:

```csharp
var cts = new CancellationTokenSource();
await cache.PublishAsync("topic", value, cancellationToken: cts.Token);
cts.Cancel();
```

## Thread Safety

PSTT is fully thread-safe:
- Concurrent Subscribe/Unsubscribe/Publish operations
- Lock-free data structures where possible
- Atomic counter operations for subscription tracking
- No blocking operations in hot paths

## Performance Characteristics

- **Subscribe**: O(1) - simple bag add operation
- **Publish**: O(n) where n = subscribers to that topic
- **Unsubscribe**: O(1) - soft-delete with IsActive flag
- **Memory**: Minimal overhead per subscription (~100 bytes)

## Testing

The library includes comprehensive tests covering:
- Remote client/server integration (subscribe, wildcard, publish, lifecycle, multi-client)
- MQTT adapter integration
- Basic pub/sub operations
- Concurrent subscriptions/publications
- Wildcard matching
- Upstream chaining (synchronous and asynchronous)
- Combined wildcards + upstream (including race-free async concurrent upstream callbacks)
- Status transitions
- Retained values
- Callback error handling
- Dispatcher behaviors
- Concurrency limits
- Edge cases and race conditions

Run tests with:
```bash
dotnet test
```

## License

[Your license here]

## Contributing

[Your contribution guidelines here]



