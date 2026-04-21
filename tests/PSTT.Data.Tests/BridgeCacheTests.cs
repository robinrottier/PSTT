namespace PSTT.Data.Tests
{
    /// <summary>
    /// Tests for BridgeCache — verifies:
    /// 1. Data flows from upstream chain through bridges into the local view.
    /// 2. Widget subscriptions on BridgeCache do NOT propagate upstream beyond the bridge.
    /// 3. Publish routing: BridgeCache.PublishAsync -> source; BridgeCache.Local.PublishAsync -> local only.
    /// 4. SetBridges mid-session tears down old bridges and configures new ones.
    /// 5. Empty bridges -> no data -> subscriptions stay Pending.
    ///
    /// Test setup mirrors the real dashboard topology:
    ///   topCache (simulates MqttCache/broker)
    ///     └─ serverCache : CacheWithWildcards (simulates ServerDataCache)
    ///          └─ dataCache : CacheWithWildcards (simulates circuit-scoped DataCache)
    ///               └─ BridgeCache
    ///                    └─ _local : CacheWithWildcards (no upstream) — widget subs here
    /// </summary>
    public class BridgeCacheTests
    {
        // ─── Setup helpers ─────────────────────────────────────────────────────────

        private static (Cache<string, string> top, Cache<string, string> server, Cache<string, string> data, BridgeCache<string, string> bridge)
            CreateChain()
        {
            var top = new CacheBuilder<string, string>().WithWildcards().WithSynchronousCallbacks().Build();
            var server = new CacheBuilder<string, string>().WithWildcards().WithSynchronousCallbacks()
                .WithUpstream(top, supportsWildcards: true).Build();
            var data = new CacheBuilder<string, string>().WithWildcards().WithSynchronousCallbacks()
                .WithUpstream(server, supportsWildcards: true).Build();
            var bridge = new BridgeCache<string, string>(data);
            return (top, server, data, bridge);
        }

        // ─── Data flow — top → local ───────────────────────────────────────────────

        [Fact]
        public async Task DataFlowsFromTopToLocal_WhenKeyMatchesBridgePattern()
        {
            var (top, _, _, bridge) = CreateChain();
            bridge.SetBridges(["ess1/servers/#"]);

            string? received = null;
            bridge.Subscribe("ess1/servers/pv/power", async s => { received = s.Value; });

            await top.PublishAsync("ess1/servers/pv/power", "1200");

            Assert.True(await TestHelper.WaitForValue("1200", () => received),
                "Expected value to flow from topCache through bridge into local subscription");
        }

        [Fact]
        public async Task MultipleMatchingKeys_AllDeliveredToLocalSubscribers()
        {
            var (top, _, _, bridge) = CreateChain();
            bridge.SetBridges(["ess1/servers/#"]);

            var received = new List<string>();
            bridge.Subscribe("ess1/servers/#", async s =>
            {
                if (!s.Status.IsPending) lock (received) received.Add(s.Value!);
            });

            await top.PublishAsync("ess1/servers/pv/power", "1200");
            await top.PublishAsync("ess1/servers/pv/voltage", "48");
            await top.PublishAsync("ess1/servers/battery/soc", "85");

            Assert.True(await TestHelper.WaitForCondition(() => { lock (received) return received.Count >= 3; }, 50));
            lock (received)
            {
                Assert.Contains("1200", received);
                Assert.Contains("48", received);
                Assert.Contains("85", received);
            }
        }

        [Fact]
        public async Task SystemTopicBridge_DashboardPrefix_DeliveredToLocal()
        {
            var (top, _, _, bridge) = CreateChain();
            bridge.SetBridges(["ess1/servers/#", "$DASHBOARD/#"]);

            string? received = null;
            bridge.Subscribe("$DASHBOARD/CLIENTS/COUNT", async s => { received = s.Value; });

            await top.PublishAsync("$DASHBOARD/CLIENTS/COUNT", "3");

            Assert.True(await TestHelper.WaitForValue("3", () => received),
                "Expected $DASHBOARD topic to flow through $DASHBOARD/# bridge pattern");
        }

        // ─── Subscription isolation — subscriptions don't propagate past bridge ───

        [Fact]
        public async Task WildcardSubscription_OnBridgeCache_DoesNotPropagateToDataCache()
        {
            var (_, _, data, bridge) = CreateChain();
            bridge.SetBridges(["ess1/servers/#"]);

            var subscribeCountBefore = data.SubscribeCount;

            // Widget subscribes '#' on the bridge — should go to _local only
            var sub = bridge.Subscribe("#", async _ => { });

            Assert.Equal(subscribeCountBefore, data.SubscribeCount);
            sub.Dispose();
        }

        [Fact]
        public async Task ExactKeySubscription_OnBridgeCache_DoesNotPropagateToDataCache()
        {
            var (_, _, data, bridge) = CreateChain();
            bridge.SetBridges(["ess1/servers/#"]);

            var subscribeCountBefore = data.SubscribeCount;

            var sub = bridge.Subscribe("ess1/servers/pv/power", async _ => { });

            Assert.Equal(subscribeCountBefore, data.SubscribeCount);
            sub.Dispose();
        }

        [Fact]
        public async Task OutOfScopeSubscription_StaysPending_EvenAfterBrokerPublishes()
        {
            var (top, _, _, bridge) = CreateChain();
            bridge.SetBridges(["ess1/servers/#"]);

            // ess1/battery is outside the bridged scope
            IStatus.StateValue lastState = IStatus.StateValue.Pending;
            var sub = bridge.Subscribe("ess1/battery/voltage", async s => { lastState = s.Status.State; });

            // Publish out-of-scope topic upstream
            await top.PublishAsync("ess1/battery/voltage", "48");

            // Wait a bit and confirm it remains Pending
            await Task.Delay(30);
            Assert.Equal(IStatus.StateValue.Pending, lastState);
            sub.Dispose();
        }

        // ─── GetValue / GetSnapshot ────────────────────────────────────────────────

        [Fact]
        public async Task GetValue_InScope_ReturnsValue()
        {
            var (top, _, _, bridge) = CreateChain();
            bridge.SetBridges(["ess1/servers/#"]);

            // Create a subscription to cause the bridge to populate _local
            var sub = bridge.Subscribe("ess1/servers/pv/power", async _ => { });
            await top.PublishAsync("ess1/servers/pv/power", "1200");

            Assert.True(await TestHelper.WaitForValue("1200", () => bridge.GetValue("ess1/servers/pv/power")));
            sub.Dispose();
        }

        [Fact]
        public async Task GetValue_OutOfScope_ReturnsNull()
        {
            var (top, _, _, bridge) = CreateChain();
            bridge.SetBridges(["ess1/servers/#"]);

            await top.PublishAsync("ess1/battery/voltage", "48");
            await Task.Delay(20);

            Assert.Null(bridge.GetValue("ess1/battery/voltage"));
        }

        [Fact]
        public async Task GetSnapshot_ContainsOnlyInScopeEntries()
        {
            var (top, _, _, bridge) = CreateChain();
            bridge.SetBridges(["ess1/servers/#"]);

            var sub = bridge.Subscribe("ess1/servers/#", async _ => { });
            await top.PublishAsync("ess1/servers/pv/power", "1200");
            await top.PublishAsync("ess1/battery/voltage", "48"); // out of scope

            Assert.True(await TestHelper.WaitForCondition(
                () => bridge.GetSnapshot().ContainsKey("ess1/servers/pv/power"), 50));

            var snapshot = bridge.GetSnapshot();
            Assert.True(snapshot.ContainsKey("ess1/servers/pv/power"));
            Assert.False(snapshot.ContainsKey("ess1/battery/voltage"));
            sub.Dispose();
        }

        // ─── Publish routing ───────────────────────────────────────────────────────

        [Fact]
        public async Task PublishAsync_OnBridgeCache_ReachesSourceCache()
        {
            var (_, _, data, bridge) = CreateChain();
            bridge.SetBridges(["ess1/servers/#"]);

            // Subscribe directly on data (bridge's _source) to verify publish lands there.
            // In production, data has forwardPublish=true toward the broker; here we just
            // verify BridgeCache routes to _source, not to _local.
            string? dataReceived = null;
            data.Subscribe("ess1/servers/switch/state", async s => { dataReceived = s.Value; });

            await bridge.PublishAsync("ess1/servers/switch/state", "ON");

            Assert.True(await TestHelper.WaitForValue("ON", () => dataReceived),
                "BridgeCache.PublishAsync should route to the source cache (_source), not _local");
        }

        [Fact]
        public async Task PublishAsync_OnLocalCache_DoesNotReachSourceOrTopCache()
        {
            var (top, _, data, bridge) = CreateChain();
            bridge.SetBridges(["ess1/servers/#"]);

            string? topReceived = null;
            string? dataReceived = null;
            top.Subscribe("ess1/servers/switch/state", async s => { topReceived = s.Value; });
            data.Subscribe("ess1/servers/switch/state", async s => { dataReceived = s.Value; });

            // Publishing on Local -> stays in _local only
            await bridge.Local.PublishAsync("ess1/servers/switch/state", "LOCAL");

            await Task.Delay(30);
            Assert.Null(topReceived);
            Assert.Null(dataReceived);
        }

        [Fact]
        public async Task LocalPublish_IsVisibleToLocalSubscriber()
        {
            var (_, _, _, bridge) = CreateChain();
            bridge.SetBridges(["ess1/servers/#"]);

            string? received = null;
            bridge.Subscribe("ess1/servers/pv/power", async s =>
            {
                if (!s.Status.IsPending) received = s.Value;
            });

            await bridge.Local.PublishAsync("ess1/servers/pv/power", "42", new Status { State = IStatus.StateValue.Active }, retain: true);

            Assert.True(await TestHelper.WaitForValue("42", () => received),
                "Local.PublishAsync should be visible to subscribers on BridgeCache");
        }

        // ─── SetBridges mid-session ────────────────────────────────────────────────

        [Fact]
        public async Task SetBridges_MidSession_OldDataCleared_NewDataArrives()
        {
            var (top, _, _, bridge) = CreateChain();
            bridge.SetBridges(["ess1/servers/#"]);

            // Subscribe and get initial data via old bridge
            string? received = null;
            var sub = bridge.Subscribe("ess1/servers/pv/power", async s =>
            {
                if (!s.Status.IsPending) received = s.Value;
            });
            await top.PublishAsync("ess1/servers/pv/power", "1200");
            Assert.True(await TestHelper.WaitForValue("1200", () => received));

            // Switch scope — old bridge torn down, _local cleared
            bridge.SetBridges(["ess1/battery/#"]);
            sub.Dispose();

            // Old data no longer in local
            await Task.Delay(10);
            Assert.Equal(0, bridge.Count);

            // New scope data should now flow
            string? newReceived = null;
            var sub2 = bridge.Subscribe("ess1/battery/soc", async s =>
            {
                if (!s.Status.IsPending) newReceived = s.Value;
            });
            await top.PublishAsync("ess1/battery/soc", "92");
            Assert.True(await TestHelper.WaitForValue("92", () => newReceived),
                "After SetBridges, new scope data should flow into local");
            sub2.Dispose();
        }

        // ─── Empty bridges ─────────────────────────────────────────────────────────

        [Fact]
        public async Task EmptyBridges_AllSubscriptionsStayPending()
        {
            var (top, _, _, bridge) = CreateChain();
            bridge.SetBridges([]); // no bridges

            IStatus.StateValue lastState = IStatus.StateValue.Pending;
            var sub = bridge.Subscribe("ess1/servers/pv/power", async s => { lastState = s.Status.State; });

            await top.PublishAsync("ess1/servers/pv/power", "1200");

            await Task.Delay(30);
            Assert.Equal(IStatus.StateValue.Pending, lastState);
            sub.Dispose();
        }
    }
}
