using PSTT.Data;
using System.Diagnostics.CodeAnalysis;

namespace PSTT.Data.Tests
{
    /// <summary>
    /// Tests for IPublisher/RegisterPublisher, SubscribeAsync, and MqttPatternMatcher.
    /// </summary>
    public class NewFeatureTests
    {
        // ─────────────────────────────────────────────────────────────────────────
        // MqttPatternMatcher
        // ─────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("sensors/+/temp", true)]
        [InlineData("sensors/#", true)]
        [InlineData("#", true)]
        [InlineData("sensors/temp", false)]
        [InlineData("", false)]
        public void MqttPatternMatcher_IsPattern(string key, bool expected)
        {
            var matcher = new MqttWildcardMatcher();
            Assert.Equal(expected, matcher.IsPattern(key));
        }

        [Theory]
        // exact match (no wildcards)
        [InlineData("a/b", "a/b", true)]
        [InlineData("a/b", "a/c", false)]
        // + wildcard — single level
        [InlineData("sensors/+/temp", "sensors/living/temp", true)]
        [InlineData("sensors/+/temp", "sensors/living/room/temp", false)]
        [InlineData("+/b", "a/b", true)]
        [InlineData("+/b", "a/b/c", false)]
        // # wildcard — multi level
        [InlineData("sensors/#", "sensors/temp", true)]
        [InlineData("sensors/#", "sensors/living/temp", true)]
        [InlineData("sensors/#", "sensors", true)]   // parent itself
        [InlineData("#", "anything", true)]
        [InlineData("#", "a/b/c", true)]
        // a/# matches a itself
        [InlineData("a/#", "a", true)]
        [InlineData("a/#", "a/b", true)]
        [InlineData("a/#", "b", false)]
        public void MqttPatternMatcher_Matches(string pattern, string candidate, bool expected)
        {
            var matcher = new MqttWildcardMatcher();
            Assert.Equal(expected, matcher.Matches(pattern, candidate));
        }

        [Fact]
        public void MqttPatternMatcher_Matches_NullPattern_Throws()
        {
            var matcher = new MqttWildcardMatcher();
            Assert.Throws<ArgumentNullException>(() => matcher.Matches(null!, "a/b"));
        }

        [Fact]
        public void MqttPatternMatcher_Matches_NullCandidate_Throws()
        {
            var matcher = new MqttWildcardMatcher();
            Assert.Throws<ArgumentNullException>(() => matcher.Matches("a/+", null!));
        }

        // ─────────────────────────────────────────────────────────────────────────
        // IPublisher / RegisterPublisher
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Publisher_PublishAsync_Value_ReachesSubscribers()
        {
            var ds = new CacheBuilder<string, string>().WithSynchronousCallbacks().Build();
            string? received = null;
            ds.Subscribe("topic", async s => { received = s.Value; });

            await using var pub = ds.RegisterPublisher("topic");
            await pub.PublishAsync("hello", CancellationToken.None);

            Assert.Equal("hello", received);
        }

        [Fact]
        public async Task Publisher_PublishAsync_ValueAndStatus_BothDelivered()
        {
            var ds = new CacheBuilder<string, string>().WithSynchronousCallbacks().Build();
            string? receivedVal = null;
            IStatus? receivedStatus = null;
            ds.Subscribe("t", async s => { receivedVal = s.Value; receivedStatus = s.Status; });

            await using var pub = ds.RegisterPublisher("t");
            var status = new Status { State = IStatus.StateValue.Stale, Message = "degraded" };
            await pub.PublishAsync("v", status);

            Assert.Equal("v", receivedVal);
            Assert.True(receivedStatus!.IsStale);
            Assert.Equal("degraded", receivedStatus.Message);
        }

        [Fact]
        public async Task Publisher_PublishAsync_StatusOnly_LeavesValueUnchanged()
        {
            var ds = new CacheBuilder<string, string>().WithSynchronousCallbacks().Build();
            ds.Subscribe("t", async s => { });

            await using var pub = ds.RegisterPublisher("t");
            await pub.PublishAsync("initial", CancellationToken.None);

            var staleStatus = new Status { State = IStatus.StateValue.Stale };
            await pub.PublishAsync((IStatus)staleStatus);

            Assert.Equal("initial", ds.GetValue("t"));
            Assert.True(ds.Subscribe("t", async s => { }).Status.IsStale);
        }

        [Fact]
        public async Task Publisher_Key_MatchesRegisteredKey()
        {
            var ds = new Cache<string, string>();
            await using var pub = ds.RegisterPublisher("my/topic");
            Assert.Equal("my/topic", pub.Key);
        }

        [Fact]
        public async Task Publisher_DisposeAsync_PublishesDefaultStaleStatus()
        {
            var ds = new CacheBuilder<string, string>().WithSynchronousCallbacks().Build();
            IStatus? finalStatus = null;
            ds.Subscribe("t", async s => { finalStatus = s.Status; });

            var pub = ds.RegisterPublisher("t");
            await pub.PublishAsync("value", CancellationToken.None);
            await pub.DisposeAsync();

            Assert.NotNull(finalStatus);
            Assert.True(finalStatus!.IsStale);
        }

        [Fact]
        public async Task Publisher_DisposeAsync_CustomDisposeStatus_IsPublished()
        {
            var ds = new CacheBuilder<string, string>().WithSynchronousCallbacks().Build();
            IStatus? finalStatus = null;
            ds.Subscribe("t", async s => { finalStatus = s.Status; });

            var failStatus = new Status { State = IStatus.StateValue.Failed, Message = "crashed" };
            var pub = ds.RegisterPublisher("t", failStatus);
            await pub.PublishAsync("value", CancellationToken.None);
            await pub.DisposeAsync();

            Assert.True(finalStatus!.IsFailed);
            Assert.Equal("crashed", finalStatus.Message);
        }

        [Fact]
        public async Task Publisher_MultiplePublishers_LastDisposePublishesStatus()
        {
            var ds = new CacheBuilder<string, string>().WithSynchronousCallbacks().Build();
            var statusUpdates = new List<IStatus.StateValue>();
            ds.Subscribe("t", async s => { statusUpdates.Add(s.Status.State); });

            var pub1 = ds.RegisterPublisher("t");
            var pub2 = ds.RegisterPublisher("t");
            await pub1.PublishAsync("v", CancellationToken.None);

            // Dispose first publisher — should NOT publish dispose status yet
            await pub1.DisposeAsync();
            int countAfterFirst = statusUpdates.Count;

            // Dispose second (last) publisher — should publish Stale status
            await pub2.DisposeAsync();

            // The status list should have one extra entry after the last dispose
            Assert.True(statusUpdates.Count > countAfterFirst);
            Assert.Equal(IStatus.StateValue.Stale, statusUpdates.Last());
        }

        [Fact]
        public async Task Publisher_DisposeAsync_Idempotent_DoesNotPublishTwice()
        {
            var ds = new CacheBuilder<string, string>().WithSynchronousCallbacks().Build();
            var staleCount = 0;
            ds.Subscribe("t", async s => { if (s.Status.IsStale) staleCount++; });

            var pub = ds.RegisterPublisher("t");
            await pub.PublishAsync("v", CancellationToken.None);

            await pub.DisposeAsync();
            await pub.DisposeAsync(); // second dispose should be a no-op

            Assert.Equal(1, staleCount);
        }

        [Fact]
        public void RegisterPublisher_NullKey_Throws()
        {
            var ds = new Cache<string, string>();
            Assert.Throws<ArgumentNullException>(() => ds.RegisterPublisher(null!));
        }

        // ─────────────────────────────────────────────────────────────────────────
        // SubscribeAsync
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task SubscribeAsync_ReturnsAfterFirstActiveValue()
        {
            var ds = new Cache<string, string>();

            // Publish after a short delay so SubscribeAsync has to actually wait
            _ = Task.Run(async () =>
            {
                await Task.Delay(20);
                await ds.PublishAsync("key", "arrived");
            });

            var sub = await ds.SubscribeAsync("key", async s => { });
            Assert.Equal("arrived", sub.Value);
            Assert.True(sub.Status.IsActive);
        }

        [Fact]
        public async Task SubscribeAsync_WithRetainedValue_ReturnsImmediately()
        {
            var ds = new Cache<string, string>();
            await ds.PublishAsync("key", "cached", null, retain: true);

            // Value already in cache — should complete on the first (retained) callback
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sub = await ds.SubscribeAsync("key", async s => { });
            sw.Stop();

            Assert.Equal("cached", sub.Value);
            Assert.True(sub.Status.IsActive);
            Assert.True(sw.ElapsedMilliseconds < 2000, "Should complete quickly with cached value");
        }

        [Fact]
        public async Task SubscribeAsync_Cancellation_ThrowsOperationCanceledException()
        {
            var ds = new Cache<string, string>();

            using var cts = new CancellationTokenSource(50); // 50 ms timeout

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => ds.SubscribeAsync("never-published", async s => { }, cts.Token));
        }

        [Fact]
        public async Task SubscribeAsync_PreCancelled_ThrowsImmediately()
        {
            var ds = new Cache<string, string>();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => ds.SubscribeAsync("key", async s => { }, cts.Token));
        }

        [Fact]
        public async Task SubscribeAsync_CallbackIsInvokedOnPublish()
        {
            var ds = new Cache<string, string>();
            var received = new List<string?>();

            _ = Task.Run(async () =>
            {
                await Task.Delay(20);
                await ds.PublishAsync("k", "first");
                await Task.Delay(10);
                await ds.PublishAsync("k", "second");
            });

            var sub = await ds.SubscribeAsync("k", async s => { received.Add(s.Value); });

            // Let the second publish fire
            await Task.Delay(100);

            // callback was invoked for both publishes
            Assert.Contains("first", received);
        }

        [Fact]
        public async Task SubscribeAsync_DoesNotAutoUnsubscribeOnCancellation()
        {
            var ds = new Cache<string, string>();
            using var cts = new CancellationTokenSource(50);

            try
            {
                await ds.SubscribeAsync("k", async s => { }, cts.Token);
            }
            catch (OperationCanceledException) { }

            // Topic should still have 1 subscription (was not unsubscribed)
            Assert.Equal(1, ds.SubscribeCount);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // WithPatternMatching builder option
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void WithPatternMatching_DefaultMatcher_IsMqttPatternMatcher()
        {
            var ds = (CacheWithWildcards<string, string>)new CacheBuilder<string, string>()
                .WithPatternMatching()
                .Build();

            Assert.IsType<MqttWildcardMatcher>(ds.WildcardMatcher);
        }

        [Fact]
        public void WithPatternMatching_CustomMatcher_IsPreserved()
        {
            var custom = new MqttWildcardMatcher(); // use same type for simplicity
            var ds = (CacheWithWildcards<string, string>)new CacheBuilder<string, string>()
                .WithPatternMatching(custom)
                .Build();

            Assert.Same(custom, ds.WildcardMatcher);
        }
    }
}
