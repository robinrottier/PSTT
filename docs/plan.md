# PSTT — Project Plan & Design Notes

---

## Summary

> *A lightweight retained-value pub/sub cache for .NET applications that need live data from upstream sources (MQTT brokers, remote servers, APIs) with transparent connection-state tracking, wildcard subscriptions, and in-process composition — without requiring an external broker.*

### Use cases

**Primary sweet spots:**
- **MAUI / WPF / WinForms** — UI binds to live values with status indicators (connecting, live, stale)
- **Blazor WASM** — client subscribes to server-side data via SignalR, with automatic reconnect/stale
- **IoT dashboards** — MQTT sensors with wildcard group subscriptions and connection health tracking
- **Embedded/offline-first** — works without network; stale values kept when connection drops
- **In-process event bus** — simple local pub/sub without a broker for component decoupling

### What PSTT does

**The unique combination is:**

1. **Retained values, in-process, no broker required** — subscribing always gives you the last known value immediately, even if the publisher ran before you subscribed. `IMemoryCache` stores values but has no notifications. Rx `BehaviorSubject` holds one value, not a keyed dictionary.

2. **Per-value IStatus (Pending → Active → Stale → Failed)** — models the reality that values come from a network source and may be unavailable. No other .NET library models this. Redis has no concept of "this key's upstream connection is broken". MQTTnet has no application-level status. Rx has errors (which terminate the stream) but no "degraded but still valid" Stale state.

3. **Upstream chaining with transparent pass-through** — a local cache can chain to an MQTT broker, a remote server, or another in-process cache. Wildcard subscriptions flow upstream automatically. No other library supports this topology natively.

4. **MQTT-style wildcard subscriptions, in-process** — `+` single-level and `#` multi-level wildcards over a dictionary, without an MQTT broker. No equivalent exists in pure .NET.

5. **Pluggable callback dispatch** — subscribers can receive callbacks on the calling thread, a thread pool, or a specific `SynchronizationContext` (e.g. UI thread for MAUI/WPF/Blazor). Rx has `ObserveOn` but it's per-subscription complexity, not a global policy.

6. **Works everywhere** — MAUI, Blazor WASM, console, ASP.NET, without any external service.

### What PSTT is NOT

- **Not a replacement for Redis/RabbitMQ** in distributed systems with many services
- **Not an enterprise message bus** (no sagas, no dead-letter queues, no guaranteed delivery)
- **Not a protocol library** (does not implement MQTT — uses MQTTnet for broker connectivity)
- **Not a streaming framework** (no backpressure, no aggregation operators)

### What already exists

| Library | What it does | Missing vs PSTT |
|---------|-------------|-----------------|
| **Redis** (StackExchange.Redis) | Pub/sub, key/value store, retained messages per-topic | Requires external broker process; no per-value status tracking; no upstream chaining; no wildcard subscriptions on pub/sub channels (only keyspace events) |
| **RabbitMQ** (.NET client) | Topic exchanges with `*`/`#` routing wildcards; fanout | Requires broker; messages are consumed (not cached); late subscribers miss values; no status model; heavy protocol |
| **MQTTnet** | MQTT pub/sub with `+`/`#` wildcards; retained messages on broker | Requires MQTT broker; no IStatus tracking; no upstream chaining; not usable as pure in-process cache without broker |
| **Rx.NET** (System.Reactive) | Composable observable sequences; `BehaviorSubject` holds last value | `BehaviorSubject` is one value, not a keyed dictionary; no wildcard subscriptions; no connection status model; complex mental model; no upstream chaining |
| **SignalR** | Hub/client message broadcast over WebSocket | No retained values; no status; no wildcards; server-only (requires ASP.NET) |
| **Orleans Streams** | Distributed pub/sub with virtual actors | Distributed systems only; no in-process use; massive dependency; no retained values |
| **MassTransit / NServiceBus** | Enterprise message bus, sagas, routing | Distributed; broker-required; designed for commands/events, not live value caching |
| **IMemoryCache / IDistributedCache** | Key/value cache | No pub/sub; no notifications; no wildcards; no status model |
| **DynamicData** (NuGet) | Reactive collections built on Rx.NET | Focuses on collection changes, not keyed live values; no upstream chaining; no status; steep Rx learning curve |
| **Akka.NET Streams** | Reactive stream processing | Distributed, complex, not a simple cache |

---

## Project Structure

```
PSTT.Data                   ← core retained-value cache library
PSTT.Mqtt                   ← MQTT adapter (uses MQTTnet)
PSTT.Remote                 ← TCP/WebSocket/SignalR remote transport
PSTT.Remote.AspNetCore      ← SignalR hub + ASP.NET DI extensions
```

**Brand:** PSTT = Pub Sub Telemetry Transport — an intentional nod to MQTT (Message Queuing **Telemetry Transport**). Distinctive, no NuGet conflicts, searchable. The MQTT kinship is explicit.

`Topic` is the conceptual name for per-key items (used in docs and comments only). Not a public type. Public API is `ICache`, `ISubscription`, `IPublisher`.

---

## Settled Design Decisions

### Class hierarchy: exact-match base, patterns as an opt-in layer

```
Cache<TKey,TValue>                  ← exact-match only; pure ConcurrentDictionary lookup
└── CacheWithPatterns<TKey,TValue>  ← adds pluggable IWildcardMatcher<TKey>
```

The base type stays fast with zero pattern overhead. `CacheWithPatterns` adds a tree structure and the `IWildcardMatcher` strategy.

### Pluggable pattern matching (`IWildcardMatcher`)

The MQTT-specific tree code is wrapped behind a strategy interface:

```csharp
public interface IWildcardMatcher<TKey>
{
    bool IsPattern(TKey key);
    bool Matches(TKey pattern, TKey candidate);
}
```

Built-in implementations:
- `MqttPatternMatcher` — `+` and `#` with `/` separator (current behaviour, just extracted)
- Future: `GlobPatternMatcher` — `*` and `**` (filesystem / Home Assistant style)

The internal tree data structure stays as an implementation detail inside `MqttPatternMatcher`. Other matchers can use linear scan, trie, regex, etc.

### `ISubscription<TKey,TValue>` — keep TKey

`TKey` stays in `ISubscription`. Rationale: callers who hold a collection of subscriptions need to identify which topic each one belongs to. Without `TKey`, `Key` would return `object` or a hardcoded `string`, losing type safety for non-string key scenarios.

### `IStatus` — keep as first-class concept (not wrapped in TValue)

`IStatus` (Pending/Active/Stale/Failed) is the mechanism by which upstream disconnects propagate. Status and Value are intentionally separate: a Stale subscription retains the last known value. Merging status into TValue would lose this useful property.

### `PublishAsync` — keep 3 overloads

| Overload | Semantics | Typical caller |
|----------|-----------|----------------|
| `(key, value, ct)` | New value, status → Active automatically | Application code, MQTT receive |
| `(key, status, ct)` | Status-only — **no value change** (e.g. mark Stale/Failed) | Remote/MQTT disconnect handler |
| `(key, value, status?, retain, ct)` | Full control | Remote protocol decode, advanced usage |

The status-only overload is critical: "connection dropped" must mark all topics Stale *without overwriting their last-known value*. Cannot collapse into a single nullable form.

`retain` stays on the full overload only. No semantic sense to "retain" a status-only publish.

### `Subscribe` vs `SubscribeAsync` — both exist

`Subscribe` returns synchronously with a handle. `SubscribeAsync` awaits the first non-Pending callback delivery, useful for startup code that needs to wait for an initial value:

```csharp
// Synchronous — returns immediately, callback fires asynchronously
var sub = cache.Subscribe("sensor/temp", OnUpdate);

// Async — awaits until first Active or Failed delivery
var sub = await cache.SubscribeAsync("sensor/temp", OnUpdate);
// Here: sub.Status is Active or Failed, never Pending
```

`SubscribeAsync` internally wraps `Subscribe` with a `TaskCompletionSource` resolved on first non-Pending callback. Supports `CancellationToken` for timeout.

### `IPublisher<TValue>` — ref-counted producer handle

Mirrors `ISubscription` on the publish side. Each call to `RegisterPublisher(key)` returns a new independent handle backed by a shared ref count per topic. When the last producer disposes, auto-publishes **Stale** to all subscribers ("my last known value is still there but I may not update it").

```csharp
public interface IPublisher<TValue> : IAsyncDisposable
{
    string Key { get; }
    Task UpdateAsync(TValue value, CancellationToken ct = default);
    Task UpdateAsync(IStatus status, CancellationToken ct = default);
    Task UpdateAsync(TValue value, IStatus? status, bool retain = false, CancellationToken ct = default);
}

// Obtained via:
IPublisher<TValue> RegisterPublisher(TKey key, IStatus? disposeStatus = null);
```

**Why Stale (not Failed) on dispose:**
- `Stale` = value still in cache, source gone away, *may* come back. Correct for MQTT/remote disconnect.
- `Failed` = hard terminal error (invalid topic, access denied). Set explicitly: `pub.UpdateAsync(Status.Failed)`.

**`retain` and `IPublisher` are orthogonal:**

| Concept | What it does |
|---------|-------------|
| `retain: true` on `PublishAsync` | Keeps the **value** in cache when subscriber count → 0 |
| `IPublisher<TValue>` | Tracks **producer ownership**; auto-publishes Stale when last producer disposes |

**When to use `IPublisher` vs `PublishAsync`:**

| Use case | Pattern |
|----------|---------|
| Long-lived producer (MQTT source, background service) | `RegisterPublisher` — hold in a field; dispose on disconnect |
| One-shot / transient publish (button press, test) | `PublishAsync` fire-and-forget — no handle needed |

---

## Rejected Designs

### TKey as IPatternKey

Would allow a single class where pattern capability comes from TKey type itself:
```csharp
var store = new Cache<string, string>();      // exact-match
var store = new Cache<MqttTopic, string>();   // pattern-aware; same class
```

**Rejected:** `string` cannot implement an interface. ~95% of usage is string keys. Wrapper types add call-site friction; the entire Remote/MQTT/AspNetCore stack is wired on `string` TKey — wide-impact refactor for minimal gain.

### Status wrapped in TValue

**Rejected:** Too deeply embedded across `CacheItem`, `PublishAsync`, remote/MQTT reconnect lifecycle. The Pending→Active→Stale→Failed state machine is core, not optional.

---

## Future Roadmap

### Lazy eviction (`WithLazyEviction`)

**What:** Last unsubscribe starts a countdown timer. If re-subscribed before expiry, the topic stays alive (upstream subscription kept, value retained). On expiry, behaves as today.

**Why useful:** Avoids upstream subscribe/unsubscribe thrash when subscribers churn rapidly. Useful for background "keep-warm" behaviour.

**API:** Builder option only — no public API change on `ICache`:
```csharp
.WithLazyEviction(TimeSpan ttl)   // e.g. 30 seconds
```

**Note:** `retain: true` (per-publish) is related but different — it keeps the *value* in cache with no upstream subscription. Lazy eviction keeps the *upstream subscription* alive temporarily.

**Implementation:** `RemoveItem` is `internal virtual` — the lazy eviction implementation overrides it to schedule a timer rather than immediately dropping. Compatible with current API.

### Persistent cache (`WithPersistence`)

**What:** Cache entries written to durable storage on update; loaded back on startup. Recovered values are immediately available (no Pending state for known keys at startup).

**Why useful:** Survive process restart without waiting for upstream re-delivery of all values. Critical for dashboard/display use cases where stale-but-available is better than blank.

**API:**
```csharp
public interface ICachePersistence<TKey, TValue>
{
    Task<IEnumerable<(TKey key, TValue value, IStatus status)>> LoadAllAsync(CancellationToken ct = default);
    Task SaveAsync(TKey key, TValue value, IStatus status, CancellationToken ct = default);
    Task DeleteAsync(TKey key, CancellationToken ct = default);
}

// Builder:
.WithPersistence(ICachePersistence<TKey, TValue> store)
```

**IStatus on recovery:** Use `State = Stale` for recovered values — truthful ("this came from storage, upstream may differ"). No new `StateValue` enum entry needed.

**Implementation:** `PublishAsync` is virtual; the persistent cache wraps or overrides it to write-through. Startup load would be a new `LoadAsync()` method on the built type, NOT on `ICache` (keeps the interface lean).

### GlobPatternMatcher

**What:** `*` single-level and `**` multi-level wildcards, filesystem / Home Assistant entity-ID style (e.g. `sensor.*`, `home.*.temperature`).

**Implementation:** New `IWildcardMatcher` implementation alongside `MqttPatternMatcher`. No changes to `CacheWithPatterns` — the pattern matching strategy is already pluggable.

### NuGet packaging

Each project becomes an independent NuGet package:
- `PSTT.Data` — core, no external dependencies
- `PSTT.Mqtt` — depends on MQTTnet
- `PSTT.Remote` — depends on `PSTT.Data`
- `PSTT.Remote.AspNetCore` — depends on `PSTT.Remote` + ASP.NET Core

Add to each `.csproj`: `PackageId`, `Version`, `Authors`, `Description`, `PackageTags`, `RepositoryUrl`.

---

## Current `ICache<TKey,TValue>` Interface (reference)

```csharp
public interface ICache<TKey, TValue>
    where TKey : notnull
{
    // Snapshot read
    TValue? GetValue(TKey key);
    bool TryGetValue(TKey key, out TValue? value);
    int Count { get; }

    // Subscribe
    ISubscription<TKey, TValue> Subscribe(TKey key, Func<ISubscription<TKey, TValue>, Task> callback);
    Task<ISubscription<TKey, TValue>> SubscribeAsync(TKey key, Func<ISubscription<TKey, TValue>, Task> callback,
        CancellationToken ct = default);
    void Unsubscribe(ISubscription<TKey, TValue> subscription);

    // Publish (fire-and-forget, any caller)
    Task PublishAsync(TKey key, TValue value, CancellationToken ct = default);
    Task PublishAsync(TKey key, IStatus status, CancellationToken ct = default);
    Task PublishAsync(TKey key, TValue value, IStatus? status, bool retain = false, CancellationToken ct = default);

    // Management
    void Clear();
}
```
