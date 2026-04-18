using System;
using System.Collections.Generic;
using System.Text;
using PSTT.Data;

namespace PSTT.Data.Tests
{
    public class DataSourceWithUpstreamTests
    {
        // Helper: create a DataSource chained to the given upstream
        private static Cache<string, string> CreateWithUpstream(ICache<string, string> upstream)
        {
            var ds = new Cache<string, string>();
            ds.SetUpstream(upstream);
            return ds;
        }

        [Fact]
        public async Task Test_SimpleSubPub()
        {
            var ds1 = new Cache<string, string>();
            var ds2 = CreateWithUpstream(ds1);

            string? receivedValue = null;
            var sub = ds2.Subscribe("key1", async (s) =>
            {
                receivedValue = s.Value;
            });

            var key = "key1";
            var value = "value1";
            await ds1.PublishAsync(key, value, null);

            // Use shared helper
            var got = await TestHelper.WaitForValue(value, () => receivedValue, 10);
            Assert.Equal(value, receivedValue);

            var value2 = "value2";
            await ds1.PublishAsync(key, value2, null);

            got = await TestHelper.WaitForValue(value2, () => receivedValue, 10);
            Assert.True(got, "Should have received value2");
            Assert.Equal(value2, receivedValue);

            ds2.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_PublishWithRetain()
        {
            var upstream = new Cache<string, string>();
            var downstream = CreateWithUpstream(upstream);

            var key = "retainKey";
            var value = "retainedValue";

            // Publish with retain on upstream before any subscriptions
            await upstream.PublishAsync(key, value, null, retain: true);
            await Task.Delay(10);

            Assert.Equal(1, upstream.Count);
            Assert.Equal(0, upstream.SubscribeCount);

            // Now subscribe on downstream - should get the retained value
            string? receivedValue = null;
            var sub = downstream.Subscribe(key, async (s) =>
            {
                receivedValue = s.Value;
            });

            // Wait for the callback to fire using shared helper
            var got = await TestHelper.WaitForValue(value, () => receivedValue);
            Assert.True(got, "Should receive retained value");
            Assert.Equal(value, receivedValue);

            downstream.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_StaleStatus()
        {
            var upstream = new Cache<string, string>();
            var downstream = CreateWithUpstream(upstream);

            var key = "staleKey";
            string? receivedValue = null;
            IStatus? receivedStatus = null;

            var sub = downstream.Subscribe(key, async (s) =>
            {
                receivedValue = s.Value;
                receivedStatus = s.Status;
            });

            // Publish with stale status
            var value = "staleValue";
            var staleStatus = new Status()
            {
                State = IStatus.StateValue.Stale,
                Message = "Data is stale",
                Code = 1
            };
            await upstream.PublishAsync(key, value, staleStatus);

            // Wait for downstream to receive
            var got = await TestHelper.WaitForValue(value, () => receivedValue);
            Assert.True(got, "Should receive stale value");
            Assert.Equal(value, receivedValue);
            Assert.NotNull(receivedStatus);
            Assert.True(receivedStatus.IsStale);
            Assert.Equal("Data is stale", receivedStatus.Message);

            // Both should still have the subscription (stale doesn't remove)
            Assert.Equal(1, upstream.Count);
            Assert.Equal(1, downstream.Count);

            downstream.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_FailStatus()
        {
            var upstream = new Cache<string, string>();
            var downstream = CreateWithUpstream(upstream);

            var key = "failKey";
            string? receivedValue = null;
            IStatus? receivedStatus = null;

            var sub = downstream.Subscribe(key, async (s) =>
            {
                receivedValue = s.Value;
                receivedStatus = s.Status;
            });

            // Publish with fail status
            var value = "failValue";
            var failStatus = new Status()
            {
                State = IStatus.StateValue.Failed,
                Message = "Connection failed",
                Code = 500
            };
            await upstream.PublishAsync(key, value, failStatus);

            // Wait for downstream to receive
            var got = await TestHelper.WaitForValue(value, () => receivedValue);
            Assert.True(got, "Should receive failed value");
            Assert.Equal(value, receivedValue);
            Assert.NotNull(receivedStatus);
            Assert.True(receivedStatus.IsFailed);
            Assert.Equal("Connection failed", receivedStatus.Message);

            // Failed status should remove the subscription from upstream
            await Task.Delay(10);
            Assert.Equal(0, upstream.Count);
            Assert.Equal(0, upstream.SubscribeCount);
        }

        [Fact]
        public async Task Test_ThreeTierChain()
        {
            // Create 3-tier: ds1 -> ds2 -> ds3
            var ds1 = new Cache<string, string>();
            var ds2 = CreateWithUpstream(ds1);
            var ds3 = CreateWithUpstream(ds2);

            var key = "chainKey";
            string? value1 = null;
            string? value2 = null;
            string? value3 = null;

            // Subscribe at all levels
            var sub1 = ds1.Subscribe(key, async (s) => { value1 = s.Value; });
            var sub2 = ds2.Subscribe(key, async (s) => { value2 = s.Value; });
            var sub3 = ds3.Subscribe(key, async (s) => { value3 = s.Value; });

            // Publish at the top level
            var testValue = "chainValue";
            await ds1.PublishAsync(key, testValue);

            // All three should receive the value
            Assert.True(await TestHelper.WaitForValue(testValue, () => value1), "ds1 should receive");
            Assert.True(await TestHelper.WaitForValue(testValue, () => value2), "ds2 should receive");
            Assert.True(await TestHelper.WaitForValue(testValue, () => value3), "ds3 should receive");

            Assert.Equal(testValue, value1);
            Assert.Equal(testValue, value2);
            Assert.Equal(testValue, value3);

            // Update with a new value
            var testValue2 = "chainValue2";
            await ds1.PublishAsync(key, testValue2);

            Assert.True(await TestHelper.WaitForValue(testValue2, () => value1), "ds1 should receive update");
            Assert.True(await TestHelper.WaitForValue(testValue2, () => value2), "ds2 should receive update");
            Assert.True(await TestHelper.WaitForValue(testValue2, () => value3), "ds3 should receive update");

            // Cleanup
            ds3.Unsubscribe(sub3);
            ds2.Unsubscribe(sub2);
            ds1.Unsubscribe(sub1);
        }

        [Fact]
        public async Task Test_TreeStructure_MultipleDownstreams()
        {
            // Create tree: ds1 -> ds2a, ds2b (both pointing to ds1)
            var upstream = new Cache<string, string>();
            var downstream1 = CreateWithUpstream(upstream);
            var downstream2 = CreateWithUpstream(upstream);

            var key = "treeKey";
            string? valueUpstream = null;
            string? valueDown1 = null;
            string? valueDown2 = null;

            // Subscribe on all three
            var subUpstream = upstream.Subscribe(key, async (s) => { valueUpstream = s.Value; });
            var subDown1 = downstream1.Subscribe(key, async (s) => { valueDown1 = s.Value; });
            var subDown2 = downstream2.Subscribe(key, async (s) => { valueDown2 = s.Value; });

            // Publish on upstream
            var testValue = "treeValue";
            await upstream.PublishAsync(key, testValue);

            // All three should receive
            Assert.True(await TestHelper.WaitForValue(testValue, () => valueUpstream), "upstream should receive");
            Assert.True(await TestHelper.WaitForValue(testValue, () => valueDown1), "downstream1 should receive");
            Assert.True(await TestHelper.WaitForValue(testValue, () => valueDown2), "downstream2 should receive");

            Assert.Equal(testValue, valueUpstream);
            Assert.Equal(testValue, valueDown1);
            Assert.Equal(testValue, valueDown2);

            // Verify subscription counts (upstream should have 3: one direct, two from downstreams)
            Assert.Equal(3, upstream.SubscribeCount);
            Assert.Equal(1, downstream1.SubscribeCount);
            Assert.Equal(1, downstream2.SubscribeCount);

            // Cleanup
            downstream2.Unsubscribe(subDown2);
            downstream1.Unsubscribe(subDown1);
            upstream.Unsubscribe(subUpstream);

            // After cleanup, all should be empty
            await Task.Delay(10);
            Assert.Equal(0, upstream.SubscribeCount);
            Assert.Equal(0, downstream1.SubscribeCount);
            Assert.Equal(0, downstream2.SubscribeCount);
        }

        [Fact]
        public async Task Test_ComplexTree_ThreeTierWithMultipleBranches()
        {
            // Create: ds1 -> ds2a, ds2b -> ds3a (under ds2a), ds3b (under ds2b)
            var ds1 = new Cache<string, string>();
            var ds2a = CreateWithUpstream(ds1);
            var ds2b = CreateWithUpstream(ds1);
            var ds3a = CreateWithUpstream(ds2a);
            var ds3b = CreateWithUpstream(ds2b);

            var key = "complexKey";
            var values = new Dictionary<string, string?>
            {
                ["ds1"] = null,
                ["ds2a"] = null,
                ["ds2b"] = null,
                ["ds3a"] = null,
                ["ds3b"] = null
            };

            // Subscribe at all levels
            var sub1 = ds1.Subscribe(key, async (s) => { values["ds1"] = s.Value; });
            var sub2a = ds2a.Subscribe(key, async (s) => { values["ds2a"] = s.Value; });
            var sub2b = ds2b.Subscribe(key, async (s) => { values["ds2b"] = s.Value; });
            var sub3a = ds3a.Subscribe(key, async (s) => { values["ds3a"] = s.Value; });
            var sub3b = ds3b.Subscribe(key, async (s) => { values["ds3b"] = s.Value; });

            // Publish at the root
            var testValue = "complexValue";
            await ds1.PublishAsync(key, testValue);

            // All should receive
            foreach (var kvp in values)
            {
                Assert.True(await TestHelper.WaitForValue(testValue, () => values[kvp.Key]), $"{kvp.Key} should receive value");
                Assert.Equal(testValue, values[kvp.Key]);
            }

            // Cleanup from bottom up
            ds3b.Unsubscribe(sub3b);
            ds3a.Unsubscribe(sub3a);
            ds2b.Unsubscribe(sub2b);
            ds2a.Unsubscribe(sub2a);
            ds1.Unsubscribe(sub1);
        }

        [Fact]
        public async Task Test_RetainWithUpstream_UnsubscribeAndResubscribe()
        {
            var upstream = new Cache<string, string>();
            var downstream = CreateWithUpstream(upstream);

            var key = "retainKey";
            var value = "initialValue";

            // Publish with retain
            await upstream.PublishAsync(key, value, null, retain: true);
            await Task.Delay(10);

            // Subscribe on downstream
            string? receivedValue = null;
            var sub1 = downstream.Subscribe(key, async (s) => { receivedValue = s.Value; });

            Assert.True(await TestHelper.WaitForValue(value, () => receivedValue));
            Assert.Equal(value, receivedValue);

            // Unsubscribe
            downstream.Unsubscribe(sub1);
            await Task.Delay(10);

            // Upstream should still have retained value
            Assert.Equal(1, upstream.Count);

            // Re-subscribe - should get the retained value again
            receivedValue = null;
            var sub2 = downstream.Subscribe(key, async (s) => { receivedValue = s.Value; });

            Assert.True(await TestHelper.WaitForValue(value, () => receivedValue), "Should get retained value on re-subscribe");
            Assert.Equal(value, receivedValue);

            downstream.Unsubscribe(sub2);
        }

        [Fact]
        public async Task Test_MultipleKeysWithUpstream()
        {
            var upstream = new Cache<string, string>();
            var downstream = CreateWithUpstream(upstream);

            var values = new Dictionary<string, string?>
            {
                ["key1"] = null,
                ["key2"] = null,
                ["key3"] = null
            };

            // Subscribe to multiple keys
            var sub1 = downstream.Subscribe("key1", async (s) => { values["key1"] = s.Value; });
            var sub2 = downstream.Subscribe("key2", async (s) => { values["key2"] = s.Value; });
            var sub3 = downstream.Subscribe("key3", async (s) => { values["key3"] = s.Value; });

            // Publish to all keys
            await upstream.PublishAsync("key1", "value1");
            await upstream.PublishAsync("key2", "value2");
            await upstream.PublishAsync("key3", "value3");

            // Verify all received
            Assert.True(await TestHelper.WaitForValue("value1", () => values["key1"]));
            Assert.True(await TestHelper.WaitForValue("value2", () => values["key2"]));
            Assert.True(await TestHelper.WaitForValue("value3", () => values["key3"]));

            Assert.Equal("value1", values["key1"]);
            Assert.Equal("value2", values["key2"]);
            Assert.Equal("value3", values["key3"]);

            // Cleanup
            downstream.Unsubscribe(sub1);
            downstream.Unsubscribe(sub2);
            downstream.Unsubscribe(sub3);
        }

        [Fact]
        public async Task Test_FailStatusInChain()
        {
            // Test that fail status propagates through chain
            var ds1 = new Cache<string, string>();
            var ds2 = CreateWithUpstream(ds1);
            var ds3 = CreateWithUpstream(ds2);

            var key = "failChainKey";
            IStatus? status1 = null;
            IStatus? status2 = null;
            IStatus? status3 = null;

            var sub1 = ds1.Subscribe(key, async (s) => { status1 = s.Status; });
            var sub2 = ds2.Subscribe(key, async (s) => { status2 = s.Status; });
            var sub3 = ds3.Subscribe(key, async (s) => { status3 = s.Status; });

            // Publish fail status
            var failStatus = new Status()
            {
                State = IStatus.StateValue.Failed,
                Message = "Upstream connection lost",
                Code = 503
            };
            await ds1.PublishAsync(key, "failedValue", failStatus);

            // Wait for propagation
            await Task.Delay(50);

            // All should have received the fail status
            Assert.NotNull(status1);
            Assert.NotNull(status2);
            Assert.NotNull(status3);
            Assert.True(status1.IsFailed);
            Assert.True(status2.IsFailed);
            Assert.True(status3.IsFailed);

            // All should be cleaned up due to fail
            Assert.Equal(0, ds1.Count);
            Assert.Equal(0, ds1.SubscribeCount);
        }

    }
}
