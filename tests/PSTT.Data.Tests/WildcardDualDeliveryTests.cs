using PSTT.Data;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PSTT.Data.Tests
{
    /// <summary>
    /// Reproduces the dual-delivery bug in <see cref="CacheWithWildcards{TKey,TValue}"/>:
    /// a '#' (wildcard) subscriber receives the same upstream value twice when an exact-key
    /// subscription for the same topic also exists on the downstream cache.
    ///
    /// Root cause — two independent delivery paths fire for a single upstream publish:
    ///   Path A  exact-key upstream sub fires → UpstreamCallbackWildcards (_isWildcard=false)
    ///           → PublishAsync → InvokeCallback → OnInvokeCallback tree-walk → '#' subscriber
    ///   Path B  '#' upstream sub fires → UpstreamCallbackWildcards (_isWildcard=true)
    ///           → InvokeCallback directly → '#' subscriber
    ///
    /// Secondary issue — InitialInvokeAsync for a newly added '#' subscriber:
    ///   Tree walk delivers values from item nodes already in the downstream tree.
    ///   _upstreamCache replay delivers the same keys again from the wildcard upstream cache.
    ///   When a key exists in both places, the '#' subscriber receives it twice on registration.
    /// </summary>
    public class WildcardDualDeliveryTests
    {
        // ─── Path A + B simultaneous delivery ─────────────────────────────────────

        [Fact]
        public async Task WildcardSubscriber_ReceivesExactlyOneDelivery_WhenExactKeyAndWildcardBothSubscribed()
        {
            // Arrange
            var upstream = new CacheBuilder<string, string>()
                .WithWildcards()
                .Build();
            var downstream = new CacheBuilder<string, string>()
                .WithWildcards()
                .WithUpstream(upstream, supportsWildcards: true)
                .Build();

            int hashCallCount = 0;

            // Exact-key sub activates Path A (OnInvokeCallback tree walk will find '#' above it).
            using var exactSub = downstream.Subscribe("sensors/temp", async _ => { });
            // '#' sub activates Path B (its own upstream sub fires independently).
            using var hashSub = downstream.Subscribe("#", async s =>
            {
                if (!s.Status.IsPending)
                    Interlocked.Increment(ref hashCallCount);
            });

            // Act — publish one value to upstream
            await upstream.PublishAsync("sensors/temp", "25.0");

            // Wait for at least one delivery to arrive
            await TestHelper.WaitForCondition(() => hashCallCount >= 1, maxRetries: 50);
            // Extra wait to surface any spurious second delivery
            await Task.Delay(50);

            // Assert — '#' subscriber must receive exactly 1 delivery, not 2
            Assert.Equal(1, hashCallCount);

            downstream.Unsubscribe(exactSub);
            downstream.Unsubscribe(hashSub);
        }

        [Fact]
        public async Task WildcardSubscriber_ReceivesExactlyOneDelivery_WhenNoExactKeySubscribed()
        {
            // Sanity check: without an exact sub, only Path B fires — should always be 1.
            var upstream = new CacheBuilder<string, string>()
                .WithWildcards()
                .Build();
            var downstream = new CacheBuilder<string, string>()
                .WithWildcards()
                .WithUpstream(upstream, supportsWildcards: true)
                .Build();

            int hashCallCount = 0;
            using var hashSub = downstream.Subscribe("#", async s =>
            {
                if (!s.Status.IsPending)
                    Interlocked.Increment(ref hashCallCount);
            });

            await upstream.PublishAsync("sensors/temp", "25.0");

            await TestHelper.WaitForCondition(() => hashCallCount >= 1, maxRetries: 50);
            await Task.Delay(50);

            Assert.Equal(1, hashCallCount);

            downstream.Unsubscribe(hashSub);
        }

        [Fact]
        public async Task WildcardSubscriber_MultipleKeys_EachReceivedExactlyOnce()
        {
            // Multiple exact subs + '#': each upstream publish must fire '#' exactly once.
            var upstream = new CacheBuilder<string, string>()
                .WithWildcards()
                .Build();
            var downstream = new CacheBuilder<string, string>()
                .WithWildcards()
                .WithUpstream(upstream, supportsWildcards: true)
                .Build();

            int hashCallCount = 0;
            using var exactSub1 = downstream.Subscribe("data/a", async _ => { });
            using var exactSub2 = downstream.Subscribe("data/b", async _ => { });
            using var exactSub3 = downstream.Subscribe("data/c", async _ => { });
            using var hashSub = downstream.Subscribe("#", async s =>
            {
                if (!s.Status.IsPending)
                    Interlocked.Increment(ref hashCallCount);
            });

            await upstream.PublishAsync("data/a", "alpha");
            await upstream.PublishAsync("data/b", "beta");
            await upstream.PublishAsync("data/c", "gamma");

            // Wait for all 3 keys to arrive (at minimum)
            await TestHelper.WaitForCondition(() => hashCallCount >= 3, maxRetries: 100);
            await Task.Delay(50);

            // 3 keys × 1 delivery each = 3. Bug would produce 6 (2 per key).
            Assert.Equal(3, hashCallCount);

            downstream.Unsubscribe(exactSub1);
            downstream.Unsubscribe(exactSub2);
            downstream.Unsubscribe(exactSub3);
            downstream.Unsubscribe(hashSub);
        }

        // ─── InitialInvokeAsync duplicate replay ──────────────────────────────────

        [Fact]
        public async Task WildcardSubscriber_InitialReplay_DoesNotDuplicateKeysAlreadyInTree()
        {
            // Reproduce the InitialInvokeAsync duplicate:
            // When '#' is subscribed after values already exist in the downstream tree,
            // InitialInvokeAsync does:
            //   1. Tree walk  — delivers keys found in the item tree
            //   2. _upstreamCache replay — delivers keys cached from the '#' upstream sub
            // If a key exists in both places it is delivered twice to the new '#' subscriber.
            var upstream = new CacheBuilder<string, string>()
                .WithWildcards()
                .Build();
            var downstream = new CacheBuilder<string, string>()
                .WithWildcards()
                .WithUpstream(upstream, supportsWildcards: true)
                .Build();

            // Pre-populate upstream before any downstream subs are registered
            await upstream.PublishAsync("sensors/temp", "20.0");
            await upstream.PublishAsync("sensors/humidity", "60.0");

            // Subscribe exact keys so the values are pulled into the downstream tree
            string? tempReceived = null;
            string? humidityReceived = null;
            using var exactTempSub = downstream.Subscribe("sensors/temp",
                async s => { tempReceived = s.Value; });
            using var exactHumiditySub = downstream.Subscribe("sensors/humidity",
                async s => { humidityReceived = s.Value; });

            // Wait until both values are in the tree
            await TestHelper.WaitForCondition(
                () => tempReceived != null && humidityReceived != null, maxRetries: 100);

            int hashCallCount = 0;
            // Now subscribe '#' — InitialInvokeAsync fires for the new subscriber
            using var hashSub = downstream.Subscribe("#", async s =>
            {
                if (!s.Status.IsPending)
                    Interlocked.Increment(ref hashCallCount);
            });

            // Allow InitialInvokeAsync replay to complete
            await Task.Delay(100);

            // 2 keys × 1 delivery each = 2. Bug produces 4 (tree walk + _upstreamCache replay each).
            Assert.Equal(2, hashCallCount);

            downstream.Unsubscribe(exactTempSub);
            downstream.Unsubscribe(exactHumiditySub);
            downstream.Unsubscribe(hashSub);
        }
    }
}
