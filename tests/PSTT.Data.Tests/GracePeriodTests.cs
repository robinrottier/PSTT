using PSTT.Data;

namespace PSTT.Data.Tests
{
    /// <summary>
    /// Tests for the UnsubscribeGracePeriod feature: the upstream subscription stays alive for a
    /// configurable window after the last local subscriber disposes, so that a rapid re-subscribe
    /// (e.g. during page navigation) reuses the upstream rather than tearing it down and rebuilding.
    /// </summary>
    public class GracePeriodTests
    {
        // ── helpers ─────────────────────────────────────────────────────────────────────

        /// <summary>Creates an upstream + a downstream cache with the given grace period.</summary>
        private static (Cache<string, string> upstream, Cache<string, string> downstream)
            BuildPair(TimeSpan gracePeriod)
        {
            var upstream = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .Build();

            var downstream = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .WithUpstream(upstream)
                .WithUnsubscribeGracePeriod(gracePeriod)
                .Build();

            return (upstream, downstream);
        }

        private static async Task WaitForCondition(Func<bool> condition, int timeoutMs = 2000, string? message = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
                await Task.Delay(10);
            Assert.True(condition(), message ?? "Condition not met within timeout");
        }

        // ── zero grace period: existing behaviour unchanged ──────────────────────────────

        [Fact]
        public async Task ZeroGracePeriod_LastUnsubscribe_RemovesItemImmediately()
        {
            var (upstream, downstream) = BuildPair(TimeSpan.Zero);

            var sub = downstream.Subscribe("key", async _ => { });
            await upstream.PublishAsync("key", "v1");

            Assert.Equal(1, downstream.Count);
            Assert.Equal(1, upstream.SubscribeCount); // upstream sub created

            downstream.Unsubscribe(sub);

            Assert.Equal(0, downstream.Count);
            Assert.Equal(0, upstream.SubscribeCount); // upstream sub torn down immediately
        }

        // ── grace period: re-subscribe within window ─────────────────────────────────────

        [Fact]
        public async Task GracePeriod_ResubscribeWithinWindow_UpstreamSubReused()
        {
            var upstream2 = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .Build();

            var downstream2 = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .WithUpstream(upstream2)
                .WithUnsubscribeGracePeriod(TimeSpan.FromSeconds(5))
                .Build();

            await upstream2.PublishAsync("key", "hello");

            // First subscribe
            string? received = null;
            var sub1 = downstream2.Subscribe("key", async s => { received = s.Value; });
            Assert.Equal(1, upstream2.SubscribeCount);

            // Unsubscribe (grace period starts but upstream sub stays alive)
            downstream2.Unsubscribe(sub1);
            Assert.Equal(0, downstream2.SubscribeCount);
            Assert.Equal(1, upstream2.SubscribeCount); // upstream NOT torn down yet

            // Re-subscribe within grace window
            string? received2 = null;
            var sub2 = downstream2.Subscribe("key", async s => { received2 = s.Value; });
            Assert.Equal(1, downstream2.SubscribeCount);
            Assert.Equal(1, upstream2.SubscribeCount); // still same upstream sub

            // Publish via upstream — re-subscribed downstream should receive it
            await upstream2.PublishAsync("key", "world");
            await WaitForCondition(() => received2 == "world", message: "Re-subscribed downstream should receive new value");

            downstream2.Unsubscribe(sub2);
        }

        [Fact]
        public async Task GracePeriod_ResubscribeWithinWindow_InitialValueDelivered()
        {
            var (upstream, downstream) = BuildPair(TimeSpan.FromSeconds(5));

            await upstream.PublishAsync("key", "cached-value");

            var sub1 = downstream.Subscribe("key", async _ => { });
            // Wait for initial value to land
            await WaitForCondition(() => downstream.GetValue("key") == "cached-value");
            downstream.Unsubscribe(sub1);

            // Re-subscribe within grace window — should get cached value immediately
            string? received = null;
            var sub2 = downstream.Subscribe("key", async s => { received = s.Value; });
            await WaitForCondition(() => received == "cached-value",
                message: "Re-subscriber should get the cached value via InitialInvokeAsync");

            downstream.Unsubscribe(sub2);
        }

        // ── grace period: expiry removes item ─────────────────────────────────────────────

        [Fact]
        public async Task GracePeriod_ExpiresWithNoResubscribe_ItemRemoved()
        {
            var (upstream, downstream) = BuildPair(TimeSpan.FromMilliseconds(50));

            var sub = downstream.Subscribe("key", async _ => { });
            await upstream.PublishAsync("key", "v1");
            Assert.Equal(1, downstream.Count);
            Assert.Equal(1, upstream.SubscribeCount);

            downstream.Unsubscribe(sub);

            // Item and upstream sub should persist during the grace window
            Assert.Equal(1, downstream.Count);
            Assert.Equal(1, upstream.SubscribeCount);

            // Wait for grace period to expire
            await WaitForCondition(() => downstream.Count == 0,
                timeoutMs: 3000,
                message: "Item should be removed after grace period expires");
            Assert.Equal(0, upstream.SubscribeCount);
        }

        [Fact]
        public async Task GracePeriod_ExpiresWithNoResubscribe_SubscribeCountZero()
        {
            var (upstream, downstream) = BuildPair(TimeSpan.FromMilliseconds(50));

            var sub = downstream.Subscribe("key", async _ => { });
            await upstream.PublishAsync("key", "v");
            downstream.Unsubscribe(sub);

            await WaitForCondition(() => downstream.SubscribeCount == 0,
                timeoutMs: 3000,
                message: "SubscribeCount should be 0 after grace period expires");
        }

        // ── grace period: churn (rapid sub/unsub/sub) ─────────────────────────────────────

        [Fact]
        public async Task GracePeriod_RapidChurn_OnlyOneUpstreamSub()
        {
            var (upstream, downstream) = BuildPair(TimeSpan.FromSeconds(5));

            await upstream.PublishAsync("key", "init");

            for (int i = 0; i < 5; i++)
            {
                var s = downstream.Subscribe("key", async _ => { });
                downstream.Unsubscribe(s);
            }

            // After churn, exactly one upstream sub should be active (the kept grace sub)
            Assert.Equal(1, upstream.SubscribeCount);

            // Final subscribe
            string? received = null;
            var finalSub = downstream.Subscribe("key", async x => { received = x.Value; });

            await upstream.PublishAsync("key", "after-churn");
            await WaitForCondition(() => received == "after-churn",
                message: "Final subscriber should receive value after churn");

            downstream.Unsubscribe(finalSub);
        }

        // ── grace period: multiple subscribers — only starts timer when last one leaves ────

        [Fact]
        public async Task GracePeriod_MultipleSubscribers_TimerStartsOnlyAfterLastLeaves()
        {
            var (upstream, downstream) = BuildPair(TimeSpan.FromMilliseconds(50));

            await upstream.PublishAsync("key", "v");

            var sub1 = downstream.Subscribe("key", async _ => { });
            var sub2 = downstream.Subscribe("key", async _ => { });

            // Remove sub1 — sub2 still alive, no grace timer yet
            downstream.Unsubscribe(sub1);
            await Task.Delay(100); // longer than grace period
            // Item should still be present because sub2 is active
            Assert.Equal(1, downstream.Count);
            Assert.Equal(1, downstream.SubscribeCount);

            // Now remove sub2 — grace timer starts
            downstream.Unsubscribe(sub2);
            Assert.Equal(0, downstream.SubscribeCount);

            await WaitForCondition(() => downstream.Count == 0,
                timeoutMs: 3000,
                message: "Item should be removed after last subscriber leaves and grace expires");
        }

        // ── grace period: Clear() cancels pending timers ──────────────────────────────────

        [Fact]
        public async Task GracePeriod_ClearCancelsPendingTimers()
        {
            var (upstream, downstream) = BuildPair(TimeSpan.FromSeconds(5));

            await upstream.PublishAsync("key", "v");
            var sub = downstream.Subscribe("key", async _ => { });
            downstream.Unsubscribe(sub);

            // Timer is pending (5 s grace); Clear() should cancel it immediately
            downstream.Clear();

            Assert.Equal(0, downstream.Count);

            // Wait a little to confirm no delayed side-effects
            await Task.Delay(50);
            Assert.Equal(0, downstream.Count);
        }

        // ── grace period: config validation ──────────────────────────────────────────────

        [Fact]
        public void WithUnsubscribeGracePeriod_Negative_Throws()
        {
            var builder = new CacheBuilder<string, string>();
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                builder.WithUnsubscribeGracePeriod(TimeSpan.FromSeconds(-1)));
        }

        [Fact]
        public void WithUnsubscribeGracePeriod_Zero_Allowed()
        {
            var cache = new CacheBuilder<string, string>()
                .WithUnsubscribeGracePeriod(TimeSpan.Zero)
                .Build();
            Assert.Equal(TimeSpan.Zero, cache.UnsubscribeGracePeriod);
        }
    }
}
