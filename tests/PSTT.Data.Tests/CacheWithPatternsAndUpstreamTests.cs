using PSTT.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace PSTT.Data.Tests
{
    /// <summary>
    /// Tests for DataSourceWithTreeAndUpstream — combines MQTT-style wildcard tree matching
    /// with upstream chaining.
    ///
    /// Key design notes:
    /// - Exact key subscriptions on the downstream create an exact-key subscription on the
    ///   upstream; the upstream callback updates the local cached value.
    /// - Wildcard subscriptions ('+' / '#') only propagate upstream when the builder is
    ///   configured with WithUpstream(supportsWildcards: true).  When false (default), wildcards
    ///   are satisfied locally via direct local publishes.
    /// - When a wildcard upstream subscription fires, callbacks may arrive concurrently from
    ///   different matching keys (expected for out-of-process or network-connected upstreams).
    ///   The downstream routes each callback via InvokeCallback rather than mutating a shared
    ///   collection.Value, so there is no race regardless of callback ordering or concurrency.
    /// - The upstream dispatcher in tests is left as the default async thread-pool dispatcher to
    ///   reflect real-world behaviour; tests use WaitForCondition rather than synchronous hacks.
    /// </summary>
    public class CacheWithPatternsAndUpstreamTests
    {
        // Helper: create a tree+upstream DS using a plain DataSource upstream (exact-key only, wildcards local)
        private static Cache<string, string> CreateTreeWithUpstream(Cache<string, string> upstream)
            => new CacheBuilder<string, string>()
                .WithWildcards()
                .WithUpstream(upstream)           // supportsWildcards defaults to false
                .Build();

        // Helper: create an async (default dispatcher) tree upstream — mirrors a real out-of-process cache
        private static Cache<string, string> CreateTreeUpstream()
            => new CacheBuilder<string, string>()
                .WithWildcards()
                .Build();

        // Helper: create a tree+upstream DS where wildcards are forwarded to a tree-capable upstream.
        // The upstream uses the default async (thread-pool) dispatcher to reflect real-world behaviour
        // where the upstream is an out-of-process or network-connected data source.
        private static Cache<string, string> CreateTreeWithWildcardUpstream(Cache<string, string> upstream)
            => new CacheBuilder<string, string>()
                .WithWildcards()
                .WithUpstream(upstream, supportsWildcards: true)
                .Build();

        // ─── Basic Integration — Exact Key ────────────────────────────────────────

        [Fact]
        public async Task Test_SimpleSubPub_ExactKey()
        {
            var upstream = new Cache<string, string>();
            var ds = CreateTreeWithUpstream(upstream);

            string? received = null;
            var sub = ds.Subscribe("topic/a", async s => { received = s.Value; });

            await upstream.PublishAsync("topic/a", "hello");

            Assert.True(await TestHelper.WaitForValue("hello", () => received));
            Assert.Equal("hello", received);

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_MultipleKeys_ExactSubscribers_ReceiveUpstreamPublish()
        {
            var upstream = new Cache<string, string>();
            var ds = CreateTreeWithUpstream(upstream);

            var received = new Dictionary<string, string?> { ["a"] = null, ["b"] = null, ["c"] = null };

            var sub1 = ds.Subscribe("data/a", async s => { received["a"] = s.Value; });
            var sub2 = ds.Subscribe("data/b", async s => { received["b"] = s.Value; });
            var sub3 = ds.Subscribe("data/c", async s => { received["c"] = s.Value; });

            await upstream.PublishAsync("data/a", "alpha");
            await upstream.PublishAsync("data/b", "beta");
            await upstream.PublishAsync("data/c", "gamma");

            Assert.True(await TestHelper.WaitForValue("alpha", () => received["a"]));
            Assert.True(await TestHelper.WaitForValue("beta", () => received["b"]));
            Assert.True(await TestHelper.WaitForValue("gamma", () => received["c"]));

            ds.Unsubscribe(sub1);
            ds.Unsubscribe(sub2);
            ds.Unsubscribe(sub3);
        }

        // ─── Local Wildcard Matching (publish directly to the downstream DS) ──────
        //
        // Wildcards always work for local publishes, regardless of upstream type.

        [Fact]
        public async Task Test_WildcardHash_LocalPublish_ReceivesValue()
        {
            var upstream = new Cache<string, string>();
            var ds = CreateTreeWithUpstream(upstream);

            var received = new List<string>();
            var sub = ds.Subscribe("sensors/#", async s => { lock (received) received.Add(s.Value!); });

            // Publish directly to the downstream (local tree matching)
            await ds.PublishAsync("sensors/temp", "22");
            await ds.PublishAsync("sensors/humidity", "55");
            await ds.PublishAsync("sensors/room1/co2", "400");

            await TestHelper.WaitForCondition(() => { lock (received) return received.Count >= 3; }, 50);

            lock (received)
            {
                Assert.Equal(3, received.Count);
                Assert.Contains("22", received);
                Assert.Contains("55", received);
                Assert.Contains("400", received);
            }

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_WildcardPlus_LocalPublish_ReceivesValue()
        {
            var upstream = new Cache<string, string>();
            var ds = CreateTreeWithUpstream(upstream);

            var received = new List<string>();
            var sub = ds.Subscribe("sensors/+/temp", async s => { lock (received) received.Add(s.Value!); });

            await ds.PublishAsync("sensors/room1/temp", "21");
            await ds.PublishAsync("sensors/room2/temp", "23");

            await TestHelper.WaitForCondition(() => { lock (received) return received.Count >= 2; }, 50);

            lock (received)
            {
                Assert.Equal(2, received.Count);
                Assert.Contains("21", received);
                Assert.Contains("23", received);
            }

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_WildcardDoesNotMatch_NonMatchingKey()
        {
            var upstream = new Cache<string, string>();
            var ds = CreateTreeWithUpstream(upstream);

            string? received = null;
            // + only matches single level — "sensors/+" should NOT match "sensors/room1/temp"
            var sub = ds.Subscribe("sensors/+", async s => { received = s.Value; });

            // Publish directly to DS: multi-level key should not match single-level wildcard
            await ds.PublishAsync("sensors/room1/temp", "21");
            await Task.Delay(20);

            Assert.Null(received);

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_MultipleSubscribers_SameWildcard_BothReceiveLocalPublish()
        {
            var upstream = new Cache<string, string>();
            var ds = CreateTreeWithUpstream(upstream);

            string? received1 = null;
            string? received2 = null;

            var sub1 = ds.Subscribe("data/#", async s => { received1 = s.Value; });
            var sub2 = ds.Subscribe("data/#", async s => { received2 = s.Value; });

            // Publish directly to the local DS
            await ds.PublishAsync("data/item1", "value1");

            Assert.True(await TestHelper.WaitForValue("value1", () => received1));
            Assert.True(await TestHelper.WaitForValue("value1", () => received2));

            ds.Unsubscribe(sub1);
            ds.Unsubscribe(sub2);
        }

        [Fact]
        public async Task Test_MultipleWildcards_BothMatch_BothFireOnLocalPublish()
        {
            var upstream = new Cache<string, string>();
            var ds = CreateTreeWithUpstream(upstream);

            string? receivedHash = null;
            string? receivedPlus = null;

            // Both wildcards match "sensors/temp"
            var subHash = ds.Subscribe("sensors/#", async s => { receivedHash = s.Value; });
            var subPlus = ds.Subscribe("sensors/+", async s => { receivedPlus = s.Value; });

            // Publish directly to local DS
            await ds.PublishAsync("sensors/temp", "25");

            Assert.True(await TestHelper.WaitForValue("25", () => receivedHash));
            Assert.True(await TestHelper.WaitForValue("25", () => receivedPlus));

            ds.Unsubscribe(subHash);
            ds.Unsubscribe(subPlus);
        }

        // ─── Wildcard + Tree Upstream (wildcard propagates through chain) ─────────
        //
        // When supportsWildcards: true is set on the builder and the upstream is tree-capable,
        // wildcard subscriptions are forwarded upstream.  The upstream uses the default async
        // (thread-pool) dispatcher to reflect a real out-of-process or network-connected source.
        // Upstream callbacks for different matching keys may arrive concurrently; the downstream
        // routes each through InvokeCallback without touching any shared mutable state, so there
        // is no race regardless of callback ordering or concurrency.

        [Fact]
        public async Task Test_WildcardHash_TreeUpstream_ReceivesUpstreamPublish()
        {
            var upstream = CreateTreeUpstream();
            var ds = CreateTreeWithWildcardUpstream(upstream);

            var received = new List<string>();
            var sub = ds.Subscribe("sensors/#", async s => { lock (received) received.Add(s.Value!); });

            await upstream.PublishAsync("sensors/temp", "22");
            await upstream.PublishAsync("sensors/humidity", "55");
            await upstream.PublishAsync("sensors/room1/co2", "400");

            await TestHelper.WaitForCondition(() => { lock (received) return received.Count >= 3; }, 100);

            lock (received)
            {
                Assert.Equal(3, received.Count);
                Assert.Contains("22", received);
                Assert.Contains("55", received);
                Assert.Contains("400", received);
            }

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_WildcardPlus_TreeUpstream_ReceivesUpstreamPublish()
        {
            var upstream = CreateTreeUpstream();
            var ds = CreateTreeWithWildcardUpstream(upstream);

            var received = new List<string>();
            var sub = ds.Subscribe("sensors/+/temp", async s => { lock (received) received.Add(s.Value!); });

            await upstream.PublishAsync("sensors/room1/temp", "21");
            await upstream.PublishAsync("sensors/room2/temp", "23");

            await TestHelper.WaitForCondition(() => { lock (received) return received.Count >= 2; }, 100);

            lock (received)
            {
                Assert.Equal(2, received.Count);
                Assert.Contains("21", received);
                Assert.Contains("23", received);
            }

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_ExactKeyAndWildcard_BothReceiveFromTreeUpstream()
        {
            var upstream = CreateTreeUpstream();
            var ds = CreateTreeWithWildcardUpstream(upstream);

            string? exactReceived = null;
            string? wildcardReceived = null;

            var subExact = ds.Subscribe("sensors/temp", async s => { exactReceived = s.Value; });
            var subWild = ds.Subscribe("sensors/#", async s => { wildcardReceived = s.Value; });

            await upstream.PublishAsync("sensors/temp", "21");

            Assert.True(await TestHelper.WaitForValue("21", () => exactReceived));
            Assert.True(await TestHelper.WaitForValue("21", () => wildcardReceived));

            ds.Unsubscribe(subExact);
            ds.Unsubscribe(subWild);
        }

        // ─── Retained Values ──────────────────────────────────────────────────────

        [Fact]
        public async Task Test_RetainedUpstreamValue_ExactKey()
        {
            var upstream = new Cache<string, string>();
            await upstream.PublishAsync("config/timeout", "30", null, retain: true);
            await Task.Delay(5);

            var ds = CreateTreeWithUpstream(upstream);

            string? received = null;
            var sub = ds.Subscribe("config/timeout", async s => { received = s.Value; });

            Assert.True(await TestHelper.WaitForValue("30", () => received));
            Assert.Equal("30", received);

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_RetainedValue_WildcardHash_TreeUpstream()
        {
            // Upstream has multiple retained values that match the wildcard.
            // InitialInvokeAsync fires callbacks concurrently (fire-and-forget) for each match.
            // The architectural fix in UpstreamCallback (routing via InvokeCallback rather than
            // mutating collection.Value) means concurrent callbacks are race-free even from an
            // async upstream.
            var upstream = CreateTreeUpstream();
            await upstream.PublishAsync("sensors/temp", "22", null, retain: true);
            await upstream.PublishAsync("sensors/humidity", "55", null, retain: true);
            await Task.Delay(5);

            var ds = CreateTreeWithWildcardUpstream(upstream);

            var received = new List<string>();
            var sub = ds.Subscribe("sensors/#", async s => { lock (received) received.Add(s.Value!); });

            await TestHelper.WaitForCondition(() => { lock (received) return received.Count >= 2; }, 100);

            lock (received)
            {
                Assert.True(received.Count >= 2, $"Expected ≥2 retained values, got {received.Count}");
                Assert.Contains("22", received);
                Assert.Contains("55", received);
            }

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_RetainedValue_WildcardPlus_TreeUpstream()
        {
            var upstream = CreateTreeUpstream();
            await upstream.PublishAsync("sensors/temp", "22", null, retain: true);
            await upstream.PublishAsync("sensors/humidity", "55", null, retain: true);
            await Task.Delay(5);

            var ds = CreateTreeWithWildcardUpstream(upstream);

            var received = new List<string>();
            var sub = ds.Subscribe("sensors/+", async s => { lock (received) received.Add(s.Value!); });

            await TestHelper.WaitForCondition(() => { lock (received) return received.Count >= 2; }, 100);

            lock (received)
            {
                Assert.True(received.Count >= 2, $"Expected ≥2 retained values, got {received.Count}");
                Assert.Contains("22", received);
                Assert.Contains("55", received);
            }

            ds.Unsubscribe(sub);
        }

        // ─── Status Propagation ───────────────────────────────────────────────────

        [Fact]
        public async Task Test_StaleStatus_PropagatesThrough()
        {
            var upstream = new Cache<string, string>();
            var ds = CreateTreeWithUpstream(upstream);

            string? receivedValue = null;
            IStatus? receivedStatus = null;
            var sub = ds.Subscribe("data/item", async s =>
            {
                receivedValue = s.Value;
                receivedStatus = s.Status;
            });

            var staleStatus = new Status { State = IStatus.StateValue.Stale, Message = "stale data", Code = 1 };
            await upstream.PublishAsync("data/item", "old-value", staleStatus);

            Assert.True(await TestHelper.WaitForValue("old-value", () => receivedValue));
            Assert.NotNull(receivedStatus);
            Assert.True(receivedStatus!.IsStale);
            Assert.Equal("stale data", receivedStatus.Message);

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_FailStatus_PropagatesThrough()
        {
            var upstream = new Cache<string, string>();
            var ds = CreateTreeWithUpstream(upstream);

            string? receivedValue = null;
            IStatus? receivedStatus = null;
            var sub = ds.Subscribe("data/item", async s =>
            {
                receivedValue = s.Value;
                receivedStatus = s.Status;
            });

            var failStatus = new Status { State = IStatus.StateValue.Failed, Message = "connection lost", Code = 503 };
            await upstream.PublishAsync("data/item", "failed-value", failStatus);

            Assert.True(await TestHelper.WaitForValue("failed-value", () => receivedValue));
            Assert.NotNull(receivedStatus);
            Assert.True(receivedStatus!.IsFailed);

            // Failed status should clean up subscriptions from upstream
            await Task.Delay(10);
            Assert.Equal(0, upstream.Count);
        }

        [Fact]
        public async Task Test_WildcardSubscriber_StaleStatus_LocalPublish()
        {
            var upstream = new Cache<string, string>();
            var ds = CreateTreeWithUpstream(upstream);

            string? receivedValue = null;
            IStatus? receivedStatus = null;
            var sub = ds.Subscribe("sensors/#", async s =>
            {
                receivedValue = s.Value;
                receivedStatus = s.Status;
            });

            // Publish normal value locally first
            await ds.PublishAsync("sensors/temp", "22");
            Assert.True(await TestHelper.WaitForValue("22", () => receivedValue));

            // Now publish stale locally
            var staleStatus = new Status { State = IStatus.StateValue.Stale, Message = "sensor offline", Code = 2 };
            await ds.PublishAsync("sensors/temp", "stale-value", staleStatus);

            Assert.True(await TestHelper.WaitForValue("stale-value", () => receivedValue));
            Assert.NotNull(receivedStatus);
            Assert.True(receivedStatus!.IsStale);

            ds.Unsubscribe(sub);
        }

        // ─── Subscription Lifecycle ───────────────────────────────────────────────

        [Fact]
        public async Task Test_Unsubscribe_CleansUpUpstreamSub()
        {
            var upstream = new Cache<string, string>();
            var ds = CreateTreeWithUpstream(upstream);

            var sub = ds.Subscribe("topic/x", async s => { });

            // Subscribing to ds creates a subscription on upstream
            Assert.Equal(1, upstream.Count);

            ds.Unsubscribe(sub);
            await Task.Delay(10);

            // After unsubscribe, upstream subscription should be removed
            Assert.Equal(0, upstream.Count);
            Assert.Equal(0, upstream.SubscribeCount);
        }

        // ─── Builder Integration ──────────────────────────────────────────────────

        [Fact]
        public async Task Test_Builder_WithTreeAndUpstream_ExactKey_WorksEndToEnd()
        {
            var upstream = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .Build();

            var ds = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .WithWildcards()
                .WithUpstream(upstream)
                .Build();

            string? received = null;
            // Exact key through upstream
            var sub = ds.Subscribe("events/click", async s => { received = s.Value; });

            await upstream.PublishAsync("events/click", "button1");

            Assert.True(await TestHelper.WaitForValue("button1", () => received));
            Assert.Equal("button1", received);

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_Builder_WithTreeAndUpstream_WildcardLocal_WorksEndToEnd()
        {
            var upstream = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .Build();

            var ds = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .WithWildcards()
                .WithUpstream(upstream)
                .Build();

            string? received = null;
            // Wildcard subscription — publish locally to confirm tree support is active
            var sub = ds.Subscribe("events/#", async s => { received = s.Value; });

            await ds.PublishAsync("events/click", "button1");

            Assert.True(await TestHelper.WaitForValue("button1", () => received));
            Assert.Equal("button1", received);

            ds.Unsubscribe(sub);
        }
    }
}
