using PSTT.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
//using static DataSource.CacheWithPatterns<TKey, TValue>;

namespace PSTT.Data.Tests
{
    public class CacheWithPatternsTests
    {
        [Fact]
        public async Task Test1()
        {
            var ds = new CacheWithWildcards<string, string?>();

            var counts = ds.GetCounts();
            Assert.Equal(1, counts.ItemCount);
            Assert.Equal(0, counts.SubCount);
            Assert.Equal(0, counts.FilterCount);

            //
            // build a 3 tier tree...
            //

            var key = "key1";
            var value = "value1";
            await ds.PublishAsync(key, value, null, true);

            counts = ds.GetCounts();
            Assert.Equal(2, counts.ItemCount);// root and "key1"
            Assert.Equal(1, counts.SubCount);// the publish with retain creaets a sub
            Assert.Equal(0, counts.FilterCount);

            string? recv1 = string.Empty;
            var sub1 = ds.Subscribe(key, async (s) =>
            {
                recv1 = s.Value;
            });

            var ret1 = await TestHelper.WaitForValue<string?>(value, () => recv1);

            Assert.True(ret1, $"Expected value for ret1 '{value}' was not received within the timeout period");
            Assert.Equal(value, recv1);

            // check the actual subscription doesnt change much...
            counts = ds.GetCounts();
            Assert.Equal(2, counts.ItemCount);// root and "key1"
            Assert.Equal(1, counts.SubCount);// the publish with retain creaets a sub
            Assert.Equal(0, counts.FilterCount);

            // 2nd tier
            var key2 = "key1/key2";
            var value2 = "value2";
            await ds.PublishAsync(key2, value2, null, true);

            string? recv2 = string.Empty;
            var sub2 = ds.Subscribe(key2, async (s) =>
            {
                recv2 = s.Value;
            });

            var ret2 = await TestHelper.WaitForValue<string?>(value2, () => recv2);

            Assert.True(ret2, $"Expected value for ret2 '{value2}' was not received within the timeout period");
            Assert.Equal(value2, recv2);

            //
            // third tier
            //
            var key3 = "key1/key2/key3";
            var value3 = "value3";
            await ds.PublishAsync(key3, value3, null, true);

            string? recv3 = string.Empty;
            var sub3 = ds.Subscribe(key3, async (s) =>
            {
                recv3 = s.Value;
            });

            var ret3 = await TestHelper.WaitForValue<string?>(value3, () => recv3);

            Assert.True(ret3, $"Expected value for ret3 '{value3}' was not received within the timeout period");
            Assert.Equal(value3, recv3);

            counts = ds.GetCounts();
            Assert.Equal(4, counts.ItemCount);
            Assert.Equal(3, counts.SubCount);
            Assert.Equal(0, counts.FilterCount);

            //
            // third , 2nd sibling
            //
            var key3a = "key1/key2/key3a";
            var value3a = "value3a";
            await ds.PublishAsync(key3a, value3a, null, true);

            string? recv3a = string.Empty;
            var sub3a = ds.Subscribe(key3a, async (s) =>
            {
                recv3a = s.Value;
            });

            var ret3a = await TestHelper.WaitForValue<string?>(value3a, () => recv3a);

            Assert.True(ret3a, $"Expected value for ret3a '{value3a}' was not received within the timeout period");
            Assert.Equal(value3a, recv3a);

            counts = ds.GetCounts();
            Assert.Equal(5, counts.ItemCount);
            Assert.Equal(4, counts.SubCount);
            Assert.Equal(0, counts.FilterCount);

            //
            // now try a filter...
            //
            var filterKey = "key1/key2/#";
            ConcurrentDictionary<string, string?> recvf = new();
            var recvCount = 0;
            var subf = ds.Subscribe(filterKey, async (s) =>
            {
                // append all values to gether so we can check we got all the expected ones
                recvf[s.Key.ToString()] = s.Value?.ToString();
                Interlocked.Increment(ref recvCount);
            });

            counts = ds.GetCounts();
            Assert.Equal(5, counts.ItemCount);
            Assert.Equal(5, counts.SubCount);
            Assert.Equal(1, counts.FilterCount);

            var retf = await TestHelper.WaitForValue<int>(3, () => recvCount);
            Assert.True(retf, $"Expected updates for '{filterKey}' was not received within the timeout period");

            Assert.Equal(3, recvf.Count);
            Assert.Equal(value2, recvf[key2]);
            Assert.Equal(value3, recvf[key3]);
            Assert.Equal(value3a, recvf[key3a]);

            // and update one of the values
            var value3b = "value3b";
            await ds.PublishAsync(key3, value3b, null, true);

            retf = await TestHelper.WaitForValue<int>(4, () => recvCount);
            Assert.True(retf, $"Expected updates for '{filterKey}' was not received within the timeout period");

            Assert.Equal(3, recvf.Count);
            Assert.Equal(value2, recvf[key2]);
            Assert.Equal(value3b, recvf[key3]);
            Assert.Equal(value3a, recvf[key3a]);

            // and add a fourth
            var key3c = "key1/key2/key3c";
            var value3c = "value3c";
            await ds.PublishAsync(key3c, value3c, null, true);

            retf = await TestHelper.WaitForValue<int>(5, () => recvCount);
            Assert.True(retf, $"Expected updates for '{filterKey}' was not received within the timeout period");

            Assert.Equal(4, recvf.Count);
            Assert.Equal(value2, recvf[key2]);
            Assert.Equal(value3b, recvf[key3]);
            Assert.Equal(value3a, recvf[key3a]);
            Assert.Equal(value3c, recvf[key3c]);
        }

        [Fact]
        public async Task SomeOddStuff()
        {
            //
            // treenode constructor exceptions...
            //
            CacheWithWildcards<string, string?>.ItemNode? nullParent = null;
            _ = Assert.ThrowsAny<InvalidOperationException>(() => new CacheWithWildcards<string, string?>.TreeNode("", nullParent));

            string nullPath = null!;
            CacheWithWildcards<string, string?>.ItemNode? parentNullPath = new("", nullPath);
            _ = Assert.ThrowsAny<InvalidOperationException>(() => new CacheWithWildcards<string, string?>.TreeNode("", parentNullPath));

            CacheWithWildcards<string, string?>.ItemNode? parentWithPath = new("", "a");
            _ = Assert.Throws<ArgumentNullException>(() => new CacheWithWildcards<string, string?>.FilterNode("", parentWithPath, null!));
            _ = Assert.Throws<ArgumentException>(() => new CacheWithWildcards<string, string?>.FilterNode("", parentWithPath, new string[0]));
            _ = Assert.Throws<ArgumentException>(() => new CacheWithWildcards<string, string?>.FilterNode("", parentWithPath, new string[] {"a"}));
        }

        [Fact]
        public async Task Test_WildcardAfterData()
        {
            var ds = new CacheWithWildcards<string, string?>();

            // Create structure FIRST
            await ds.PublishAsync("topic/a", "valueA", null, true);
            await ds.PublishAsync("topic/b", "valueB", null, true);
            await ds.PublishAsync("topic/c", "valueC", null, true);

            // Then subscribe with wildcard
            var filterKey = "topic/#";
            ConcurrentDictionary<string, string?> recvf = new();
            var recvCount = 0;
            var subf = ds.Subscribe(filterKey, async (s) =>
            {
                recvf[s.Key.ToString()] = s.Value?.ToString();
                Interlocked.Increment(ref recvCount);
            });

            // Should receive all three existing values
            var retf = await TestHelper.WaitForValue<int>(3, () => recvCount, 20);
            Assert.True(retf, "Should receive 3 existing values");
            Assert.Equal(3, recvf.Count);
            Assert.Equal("valueA", recvf["topic/a"]);
            Assert.Equal("valueB", recvf["topic/b"]);
            Assert.Equal("valueC", recvf["topic/c"]);

            // Add a new value - should also be received
            await ds.PublishAsync("topic/d", "valueD", null, true);
            retf = await TestHelper.WaitForValue<int>(4, () => recvCount, 20);
            Assert.True(retf, "Should receive new value");
            Assert.Equal("valueD", recvf["topic/d"]);

            ds.Unsubscribe(subf);
        }

        [Fact]
        public async Task Test_MultiLevelWildcardAfterData()
        {
            var ds = new CacheWithWildcards<string, string?>();

            // Create deep structure FIRST
            await ds.PublishAsync("root/level1/item1", "value1", null, true);
            await ds.PublishAsync("root/level1/item2", "value2", null, true);
            await ds.PublishAsync("root/level2/item3", "value3", null, true);
            await ds.PublishAsync("root/level2/sub/item4", "value4", null, true);

            // Subscribe with multi-level wildcard
            var filterKey = "root/#";
            ConcurrentDictionary<string, string?> recvf = new();
            var recvCount = 0;
            var subf = ds.Subscribe(filterKey, async (s) =>
            {
                recvf[s.Key.ToString()] = s.Value?.ToString();
                Interlocked.Increment(ref recvCount);
            });

            // Should receive all descendants
            var retf = await TestHelper.WaitForValue<int>(4, () => recvCount, 20);
            Assert.True(retf, "Should receive all 4 descendants");
            Assert.Equal(4, recvf.Count);
            Assert.Equal("value1", recvf["root/level1/item1"]);
            Assert.Equal("value2", recvf["root/level1/item2"]);
            Assert.Equal("value3", recvf["root/level2/item3"]);
            Assert.Equal("value4", recvf["root/level2/sub/item4"]);

            ds.Unsubscribe(subf);
        }

        [Fact]
        public async Task Test_SingleLevelWildcardMatching()
        {
            var ds = new CacheWithWildcards<string, string?>();

            // Create structure with different depths
            await ds.PublishAsync("sensor/temp", "20C", null, true);
            await ds.PublishAsync("sensor/humidity", "65%", null, true);
            await ds.PublishAsync("sensor/pressure/value", "1013mb", null, true);

            // Subscribe with single-level wildcard - should only match direct children
            var filterKey = "sensor/+";
            ConcurrentDictionary<string, string?> recvf = new();
            var recvCount = 0;
            var subf = ds.Subscribe(filterKey, async (s) =>
            {
                recvf[s.Key.ToString()] = s.Value?.ToString();
                Interlocked.Increment(ref recvCount);
            });

            // Should only receive temp and humidity, not pressure/value
            var retf = await TestHelper.WaitForValue<int>(2, () => recvCount, 20);
            Assert.True(retf, "Should receive 2 matches for single-level wildcard");
            Assert.Equal(2, recvf.Count);
            Assert.Equal("20C", recvf["sensor/temp"]);
            Assert.Equal("65%", recvf["sensor/humidity"]);

            ds.Unsubscribe(subf);
        }

        [Fact]
        public async Task Test_MultipleWildcardsInPattern()
        {
            var ds = new CacheWithWildcards<string, string?>();

            // Create structure
            await ds.PublishAsync("home/room1/temp", "value1", null, true);
            await ds.PublishAsync("home/room2/temp", "value2", null, true);
            await ds.PublishAsync("home/room1/humidity", "value3", null, true);

            // Pattern with two single-level wildcards
            var filterKey = "home/+/temp";
            ConcurrentDictionary<string, string?> recvf = new();
            var recvCount = 0;
            var subf = ds.Subscribe(filterKey, async (s) =>
            {
                recvf[s.Key.ToString()] = s.Value?.ToString();
                Interlocked.Increment(ref recvCount);
            });

            // Should match room1/temp and room2/temp
            var retf = await TestHelper.WaitForValue<int>(2, () => recvCount, 20);
            Assert.True(retf, "Should receive 2 matches");
            Assert.Equal(2, recvf.Count);
            Assert.Equal("value1", recvf["home/room1/temp"]);
            Assert.Equal("value2", recvf["home/room2/temp"]);

            ds.Unsubscribe(subf);
        }

        [Fact]
        public async Task Test_StaleStatusPropagation()
        {
            var ds = new CacheWithWildcards<string, string?>();

            var key = "a/b/c";
            IStatus? receivedStatus = null;
            var sub = ds.Subscribe(key, async (s) =>
            {
                receivedStatus = s.Status;
            });

            // Publish with stale status
            var staleStatus = new Status()
            {
                State = IStatus.StateValue.Stale,
                Message = "Connection stale",
                Code = 1
            };
            await ds.PublishAsync(key, "staleValue", staleStatus, true);

            var ret = await TestHelper.WaitForValue<bool>(true, () => receivedStatus?.IsStale ?? false, 20);
            Assert.True(ret, "Should receive stale status");
            Assert.NotNull(receivedStatus);
            Assert.True(receivedStatus.IsStale);
            Assert.Equal("Connection stale", receivedStatus.Message);

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_FailStatusPropagation()
        {
            var ds = new CacheWithWildcards<string, string?>();

            var key = "a/b/c";
            IStatus? receivedStatus = null;
            var sub = ds.Subscribe(key, async (s) =>
            {
                receivedStatus = s.Status;
            });

            // Publish with fail status
            var failStatus = new Status()
            {
                State = IStatus.StateValue.Failed,
                Message = "Connection failed",
                Code = 500
            };
            await ds.PublishAsync(key, "failValue", failStatus, false);

            var ret = await TestHelper.WaitForValue<bool>(true, () => receivedStatus?.IsFailed ?? false, 20);
            Assert.True(ret, "Should receive fail status");
            Assert.NotNull(receivedStatus);
            Assert.True(receivedStatus.IsFailed);

            // Failed status should clean up
            await Task.Delay(50);
            Assert.Equal(0, ds.SubscribeCount);
        }

        [Fact]
        public async Task Test_StatusPropagationThroughWildcard()
        {
            var ds = new CacheWithWildcards<string, string?>();

            // Create initial value FIRST
            await ds.PublishAsync("device/status", "ok", null, true);

            // Then subscribe with wildcard
            ConcurrentDictionary<string, IStatus?> statusMap = new();
            var subf = ds.Subscribe("device/#", async (s) =>
            {
                statusMap[s.Key.ToString()] = s.Status;
            });

            await Task.Delay(20);

            // Update with stale status
            var staleStatus = new Status()
            {
                State = IStatus.StateValue.Stale,
                Message = "Stale data"
            };
            await ds.PublishAsync("device/status", "warning", staleStatus, true);

            var ret = await TestHelper.WaitForValue<bool>(true, () => statusMap.ContainsKey("device/status") && (statusMap["device/status"]?.IsStale ?? false), 20);
            Assert.True(ret, "Wildcard should receive stale status");
            Assert.True(statusMap["device/status"]!.IsStale);

            ds.Unsubscribe(subf);
        }

        [Fact]
        public async Task Test_RetainAtDifferentTreeLevels()
        {
            var ds = new CacheWithWildcards<string, string?>();

            // Publish with retain at different levels
            await ds.PublishAsync("a", "valueA", null, true);
            await ds.PublishAsync("a/b", "valueB", null, true);
            await ds.PublishAsync("a/b/c", "valueC", null, true);

            await Task.Delay(20);

            // Now subscribe to each - should get retained values
            string? recvA = null, recvB = null, recvC = null;
            var subA = ds.Subscribe("a", async (s) => { recvA = s.Value; });
            var subB = ds.Subscribe("a/b", async (s) => { recvB = s.Value; });
            var subC = ds.Subscribe("a/b/c", async (s) => { recvC = s.Value; });

            Assert.True(await TestHelper.WaitForValue("valueA", () => recvA, 20));
            Assert.True(await TestHelper.WaitForValue("valueB", () => recvB, 20));
            Assert.True(await TestHelper.WaitForValue("valueC", () => recvC, 20));

            ds.Unsubscribe(subA);
            ds.Unsubscribe(subB);
            ds.Unsubscribe(subC);
        }

        [Fact]
        public async Task Test_WildcardCountTracking()
        {
            var ds = new CacheWithWildcards<string, string?>();

            // Publish some data FIRST
            await ds.PublishAsync("sys/cpu", "50%", null, true);
            await ds.PublishAsync("sys/mem", "2GB", null, true);

            var recvCount = 0;
            var subf = ds.Subscribe("sys/#", async (s) => { recvCount++; });

            await TestHelper.WaitForValue<int>(2, () => recvCount, 20);
            Assert.Equal(2, recvCount);

            var countsBefore = ds.GetCounts();
            Assert.Equal(1, countsBefore.FilterCount);

            // Unsubscribe
            ds.Unsubscribe(subf);
            await Task.Delay(20);

            var countsAfter = ds.GetCounts();
            Assert.Equal(0, countsAfter.FilterCount);

            // Publish new value - should NOT increase count
            await ds.PublishAsync("sys/disk", "100GB", null, true);
            await Task.Delay(20);
            Assert.Equal(2, recvCount); // Still 2
        }

        [Fact]
        public async Task Test_EmptyPathSegments()
        {
            var ds = new CacheWithWildcards<string, string?>();

            // Keys with single segments
            await ds.PublishAsync("a", "valueA", null, true);
            await ds.PublishAsync("b", "valueB", null, true);

            string? recvA = null, recvB = null;
            var subA = ds.Subscribe("a", async (s) => { recvA = s.Value; });
            var subB = ds.Subscribe("b", async (s) => { recvB = s.Value; });

            Assert.True(await TestHelper.WaitForValue("valueA", () => recvA, 20));
            Assert.True(await TestHelper.WaitForValue("valueB", () => recvB, 20));

            ds.Unsubscribe(subA);
            ds.Unsubscribe(subB);
        }

        [Fact]
        public async Task Test_DeepNesting()
        {
            var ds = new CacheWithWildcards<string, string?>();

            // Create deep hierarchy
            var deepKey = "level1/level2/level3/level4/level5";
            await ds.PublishAsync(deepKey, "deepValue", null, true);

            string? recv = null;
            var sub = ds.Subscribe(deepKey, async (s) => { recv = s.Value; });

            Assert.True(await TestHelper.WaitForValue("deepValue", () => recv, 20));

            // Wildcard at intermediate level
            Dictionary<string, string?> recvf = new();
            var subf = ds.Subscribe("level1/level2/#", async (s) =>
            {
                recvf[s.Key.ToString()] = s.Value?.ToString();
            });

            // Should receive the existing deep value
            Assert.True(await TestHelper.WaitForValue<int>(1, () => recvf.Count, 20));
            Assert.Equal("deepValue", recvf[deepKey]);

            ds.Unsubscribe(sub);
            ds.Unsubscribe(subf);
        }

        [Fact]
        public async Task Test_DirectAndWildcardSubscriptions()
        {
            var ds = new CacheWithWildcards<string, string?>();

            var key = "app/config/setting";
            string? directRecv = null;
            string? wildcardRecv = null;

            // Direct subscription
            var subDirect = ds.Subscribe(key, async (s) => { directRecv = s.Value; });

            // Wildcard subscription
            Dictionary<string, string?> wildcardMap = new();
            var subWildcard = ds.Subscribe("app/#", async (s) =>
            {
                wildcardMap[s.Key.ToString()] = s.Value?.ToString();
                if (s.Key.ToString() == key)
                    wildcardRecv = s.Value?.ToString();
            });

            // Publish
            await ds.PublishAsync(key, "value1", null, true);

            Assert.True(await TestHelper.WaitForValue("value1", () => directRecv, 20));
            Assert.True(await TestHelper.WaitForValue("value1", () => wildcardRecv, 20));

            // Update
            await ds.PublishAsync(key, "value2", null, true);

            Assert.True(await TestHelper.WaitForValue("value2", () => directRecv, 20));
            Assert.True(await TestHelper.WaitForValue("value2", () => wildcardRecv, 20));

            ds.Unsubscribe(subDirect);
            ds.Unsubscribe(subWildcard);
        }

        [Fact]
        public async Task Test_OverlappingWildcards()
        {
            var ds = new CacheWithWildcards<string, string?>();

            // Publish data FIRST
            await ds.PublishAsync("data/sensors/temp/value", "25C", null, true);

            var count1 = 0;
            var count2 = 0;
            var count3 = 0;

            // Subscribe with overlapping wildcards
            var sub1 = ds.Subscribe("data/#", async (s) => { count1++; });
            var sub2 = ds.Subscribe("data/sensors/#", async (s) => { count2++; });
            var sub3 = ds.Subscribe("data/sensors/temp/#", async (s) => { count3++; });

            // All three should receive the existing value
            Assert.True(await TestHelper.WaitForValue<int>(1, () => count1, 20));
            Assert.True(await TestHelper.WaitForValue<int>(1, () => count2, 20));
            Assert.True(await TestHelper.WaitForValue<int>(1, () => count3, 20));

            // Publish update - all should receive again
            await ds.PublishAsync("data/sensors/temp/value", "26C", null, true);

            Assert.True(await TestHelper.WaitForValue<int>(2, () => count1, 20));
            Assert.True(await TestHelper.WaitForValue<int>(2, () => count2, 20));
            Assert.True(await TestHelper.WaitForValue<int>(2, () => count3, 20));

            ds.Unsubscribe(sub1);
            ds.Unsubscribe(sub2);
            ds.Unsubscribe(sub3);
        }

        [Fact]
        public async Task Test_WildcardSubscribeBeforePublish()
        {
            var ds = new CacheWithWildcards<string, string?>();

            // Subscribe to wildcard BEFORE any data exists
            var received = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>();
            var sub = ds.Subscribe("sensors/#", async (s) =>
            {
                received[s.Key.ToString()] = s.Value;
            });

            // Now publish data - should be received by wildcard
            await ds.PublishAsync("sensors/temp", "25C", null, true);
            await ds.PublishAsync("sensors/humidity", "60%", null, true);
            await ds.PublishAsync("sensors/pressure/value", "1013mb", null, true);

            Assert.True(await TestHelper.WaitForValue<int>(3, () => received.Count, 20));
            Assert.Equal("25C", received["sensors/temp"]);
            Assert.Equal("60%", received["sensors/humidity"]);
            Assert.Equal("1013mb", received["sensors/pressure/value"]);

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_MultipleSubscribersToSameWildcard()
        {
            var ds = new CacheWithWildcards<string, string?>();

            var count1 = 0;
            var count2 = 0;
            var count3 = 0;

            var sub1 = ds.Subscribe("topic/#", async (s) => { count1++; });
            var sub2 = ds.Subscribe("topic/#", async (s) => { count2++; });
            var sub3 = ds.Subscribe("topic/#", async (s) => { count3++; });

            await ds.PublishAsync("topic/a", "value1", null, true);
            await ds.PublishAsync("topic/b/c", "value2", null, true);

            Assert.True(await TestHelper.WaitForValue<int>(2, () => count1, 20));
            Assert.True(await TestHelper.WaitForValue<int>(2, () => count2, 20));
            Assert.True(await TestHelper.WaitForValue<int>(2, () => count3, 20));

            // Unsubscribe one - others should still work
            ds.Unsubscribe(sub2);

            await ds.PublishAsync("topic/d", "value3", null, true);

            Assert.True(await TestHelper.WaitForValue<int>(3, () => count1, 20));
            Assert.Equal(2, count2); // Should not increase
            Assert.True(await TestHelper.WaitForValue<int>(3, () => count3, 20));

            ds.Unsubscribe(sub1);
            ds.Unsubscribe(sub3);
        }

        [Fact]
        public async Task Test_NestedWildcards()
        {
            var config = new CacheConfig<string, string?> 
            { 
                MaxCallbackConcurrency = -1 
            };
            var ds = new CacheWithWildcards<string, string?>(config);

            ConcurrentDictionary<string, ConcurrentBag<string>> received = new();

            var sub1 = ds.Subscribe("building/#", async (s) =>
            {
                var bag = received.GetOrAdd("building/#", _ => new ConcurrentBag<string>());
                bag.Add(s.Key.ToString());
            });

            var sub2 = ds.Subscribe("building/floor1/#", async (s) =>
            {
                var bag = received.GetOrAdd("building/floor1/#", _ => new ConcurrentBag<string>());
                bag.Add(s.Key.ToString());
            });

            var sub3 = ds.Subscribe("building/floor1/room1/#", async (s) =>
            {
                var bag = received.GetOrAdd("building/floor1/room1/#", _ => new ConcurrentBag<string>());
                bag.Add(s.Key.ToString());
            });

            // Publish at different levels
            await ds.PublishAsync("building/floor1/room1/temp", "20C", null, true);
            await ds.PublishAsync("building/floor1/room2/temp", "21C", null, true);
            await ds.PublishAsync("building/floor2/room1/temp", "22C", null, true);

            // Allow time for async callbacks to complete
            // and check callbacks complete
            await Task.Delay(50);
            await TestHelper.WaitForValue<int>(0, () => ds.InCallback, 20);

            // building/# should receive all 3
            await TestHelper.WaitForValue<int>(3, () => received["building/#"].Count, 20);
            Assert.Equal(3, received["building/#"].Count);

            // building/floor1/# should receive 2 (room1 and room2)
            await TestHelper.WaitForValue<int>(2, () => received["building/floor1/#"].Count, 20);
            Assert.Equal(2, received["building/floor1/#"].Count);

            // building/floor1/room1/# should receive only 1
            Assert.Equal(1, received["building/floor1/room1/#"].Count);
            Assert.Contains("building/floor1/room1/temp", received["building/floor1/room1/#"]);

            ds.Unsubscribe(sub1);
            ds.Unsubscribe(sub2);
            ds.Unsubscribe(sub3);
        }

        [Fact]
        public async Task Test_SingleLevelWildcardExactMatching()
        {
            var ds = new CacheWithWildcards<string, string?>();

            ConcurrentDictionary<string, string?> received = new();
            var sub = ds.Subscribe("devices/+/status", async (s) =>
            {
                received[s.Key.ToString()] = s.Value;
            });

            // Should match
            await ds.PublishAsync("devices/device1/status", "online", null, true);
            await ds.PublishAsync("devices/device2/status", "offline", null, true);

            // Should NOT match (too deep)
            await ds.PublishAsync("devices/device1/status/details", "info", null, true);

            // Should NOT match (not enough levels)
            await ds.PublishAsync("devices/status", "general", null, true);

            await Task.Delay(50);

            Assert.Equal(2, received.Count);
            Assert.Equal("online", received["devices/device1/status"]);
            Assert.Equal("offline", received["devices/device2/status"]);

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_ComplexWildcardPattern()
        {
            var ds = new CacheWithWildcards<string, string?>();

            ConcurrentDictionary<string, string?> received = new();
            // Pattern: home/+/sensors/+/value
            var sub = ds.Subscribe("home/+/sensors/+/value", async (s) =>
            {
                received[s.Key.ToString()] = s.Value;
            });

            // Should match
            await ds.PublishAsync("home/livingroom/sensors/temp/value", "22C", null, true);
            await ds.PublishAsync("home/bedroom/sensors/humidity/value", "55%", null, true);

            // Should NOT match
            await ds.PublishAsync("home/kitchen/sensors/temp", "23C", null, true);
            await ds.PublishAsync("home/livingroom/sensors/temp/value/extra", "data", null, true);

            await Task.Delay(50);

            Assert.Equal(2, received.Count);
            Assert.Equal("22C", received["home/livingroom/sensors/temp/value"]);
            Assert.Equal("55%", received["home/bedroom/sensors/humidity/value"]);

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_WildcardWithMixedSubscriptions()
        {
            var ds = new CacheWithWildcards<string, string?>();

            string? directValue = null;
            ConcurrentDictionary<string, string?> wildcardValues = new();

            // Direct subscription
            var subDirect = ds.Subscribe("app/config/db/connection", async (s) =>
            {
                directValue = s.Value;
            });

            // Wildcard subscription
            var subWildcard = ds.Subscribe("app/config/#", async (s) =>
            {
                wildcardValues[s.Key.ToString()] = s.Value;
            });

            // Publish to the direct subscription
            await ds.PublishAsync("app/config/db/connection", "localhost:5432", null, true);

            // Both should receive
            Assert.True(await TestHelper.WaitForValue("localhost:5432", () => directValue, 20));
            Assert.True(await TestHelper.WaitForValue<bool>(true, () => wildcardValues.ContainsKey("app/config/db/connection"), 20));
            Assert.Equal("localhost:5432", wildcardValues["app/config/db/connection"]);

            // Publish to another path
            await ds.PublishAsync("app/config/cache/enabled", "true", null, true);

            // Only wildcard should receive this one
            await Task.Delay(20);
            Assert.Equal("localhost:5432", directValue); // Unchanged
            Assert.Equal("true", wildcardValues["app/config/cache/enabled"]);

            ds.Unsubscribe(subDirect);
            ds.Unsubscribe(subWildcard);
        }

        [Fact]
        public async Task Test_TreeCleanupAfterUnsubscribe()
        {
            var ds = new CacheWithWildcards<string, string?>();

            // Create some subscriptions
            var sub1 = ds.Subscribe("root/branch1/leaf1", async (s) => { });
            var sub2 = ds.Subscribe("root/branch1/leaf2", async (s) => { });
            var sub3 = ds.Subscribe("root/branch2/leaf1", async (s) => { });

            var counts = ds.GetCounts();
            Assert.Equal(7, counts.ItemCount); // RootNode + root + branch1 + branch2 + 3 leafs

            // Unsubscribe and check cleanup
            ds.Unsubscribe(sub1);
            counts = ds.GetCounts();
            Assert.Equal(6, counts.ItemCount); // leaf1 removed, branch1 still has leaf2

            ds.Unsubscribe(sub2);
            counts = ds.GetCounts();
            // leaf2 removed but branch1 stays (parent nodes are kept even without children or subscriptions)
            // Remaining: RootNode + root + branch1 + branch2 + leaf1_under_branch2
            // = childless branchess now cleaned up ==> RootNode+root+branch2+leaf1_under_branch2
            Assert.Equal(4, counts.ItemCount);

            ds.Unsubscribe(sub3);
            counts = ds.GetCounts();
            // leaf1 removed, nothing left except RootNode
            Assert.Equal(1, counts.ItemCount);
        }

        [Fact]
        public async Task Test_WildcardCleanupAfterUnsubscribe()
        {
            var ds = new CacheWithWildcards<string, string?>();

            var sub1 = ds.Subscribe("topic/#", async (s) => { });
            var sub2 = ds.Subscribe("topic/sub/#", async (s) => { });

            var counts = ds.GetCounts();
            Assert.Equal(2, counts.FilterCount);

            ds.Unsubscribe(sub1);
            counts = ds.GetCounts();
            Assert.Equal(1, counts.FilterCount);

            ds.Unsubscribe(sub2);
            counts = ds.GetCounts();
            Assert.Equal(0, counts.FilterCount);
        }

        [Fact]
        public async Task Test_StatusPropagationThroughMultipleWildcards()
        {
            var ds = new CacheWithWildcards<string, string?>();

            ConcurrentDictionary<string, IStatus?> status1 = new();
            ConcurrentDictionary<string, IStatus?> status2 = new();

            var sub1 = ds.Subscribe("sys/#", async (s) =>
            {
                status1[s.Key.ToString()] = s.Status;
            });

            var sub2 = ds.Subscribe("sys/services/#", async (s) =>
            {
                status2[s.Key.ToString()] = s.Status;
            });

            // Publish with stale status
            var staleStatus = new Status()
            {
                State = IStatus.StateValue.Stale,
                Message = "Service degraded"
            };
            await ds.PublishAsync("sys/services/api", "running", staleStatus, true);

            await Task.Delay(50);

            // Both wildcards should receive the stale status
            Assert.True(status1.ContainsKey("sys/services/api"));
            Assert.True(status1["sys/services/api"]!.IsStale);
            Assert.Equal("Service degraded", status1["sys/services/api"]!.Message);

            Assert.True(status2.ContainsKey("sys/services/api"));
            Assert.True(status2["sys/services/api"]!.IsStale);

            ds.Unsubscribe(sub1);
            ds.Unsubscribe(sub2);
        }

        [Fact]
        public async Task Test_RetainWithWildcardSubscription()
        {
            var ds = new CacheWithWildcards<string, string?>();

            // Publish with retain
            await ds.PublishAsync("config/server/port", "8080", null, true);
            await ds.PublishAsync("config/server/host", "localhost", null, true);
            await ds.PublishAsync("config/db/name", "mydb", null, true);

            // Subscribe with wildcard after data published
            ConcurrentDictionary<string, string?> received = new();
            var sub = ds.Subscribe("config/#", async (s) =>
            {
                received[s.Key.ToString()] = s.Value;
            });

            // Should receive all retained values
            Assert.True(await TestHelper.WaitForValue<int>(3, () => received.Count, 20));
            Assert.Equal("8080", received["config/server/port"]);
            Assert.Equal("localhost", received["config/server/host"]);
            Assert.Equal("mydb", received["config/db/name"]);

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_MultipleUpdatesToSameKey()
        {
            var ds = new CacheWithWildcards<string, string?>();

            ConcurrentBag<string?> values = new();
            var sub = ds.Subscribe("sensor/#", async (s) =>
            {
                values.Add(s.Value);
            });

            // Rapidly update same key
            await ds.PublishAsync("sensor/temp", "20C", null, true);
            await ds.PublishAsync("sensor/temp", "21C", null, true);
            await ds.PublishAsync("sensor/temp", "22C", null, true);
            await ds.PublishAsync("sensor/temp", "23C", null, true);

            await Task.Delay(100);

            // Should receive all updates (though some might be skipped due to concurrency)
            Assert.True(values.Count >= 1);
            Assert.Contains("23C", values); // At least the last one should be there

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_EdgeCase_RootLevelKey()
        {
            var ds = new CacheWithWildcards<string, string?>();

            string? value = null;
            var sub = ds.Subscribe("rootkey", async (s) =>
            {
                value = s.Value;
            });

            await ds.PublishAsync("rootkey", "rootvalue", null, true);

            Assert.True(await TestHelper.WaitForValue("rootvalue", () => value, 20));

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_EdgeCase_RootLevelKey_RootWildcard()
        {
            var ds = new CacheWithWildcards<string, string?>();

            string key = "";
            string? value = null;
            var sub = ds.Subscribe("#", async (s) =>
            {
                key = s.Key.ToString();
                value = s.Value;
            });

            await ds.PublishAsync("rootkey", "rootvalue", null, true);

            Assert.True(await TestHelper.WaitForValue("rootvalue", () => value, 20));
            Assert.Equal("rootkey", key);

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_EdgeCase_DeepNesting20Levels()
        {
            var ds = new CacheWithWildcards<string, string?>();

            var deepKey = "l1/l2/l3/l4/l5/l6/l7/l8/l9/l10/l11/l12/l13/l14/l15/l16/l17/l18/l19/l20";
            string? value = null;
            var sub = ds.Subscribe(deepKey, async (s) =>
            {
                value = s.Value;
            });

            await ds.PublishAsync(deepKey, "deepvalue", null, true);

            Assert.True(await TestHelper.WaitForValue("deepvalue", () => value, 20));

            // Wildcard at middle level
            ConcurrentDictionary<string, string?> wildcardValues = new();
            var subWildcard = ds.Subscribe("l1/l2/l3/l4/l5/#", async (s) =>
            {
                wildcardValues[s.Key.ToString()] = s.Value;
            });

            await Task.Delay(50);

            Assert.True(wildcardValues.ContainsKey(deepKey));
            Assert.Equal("deepvalue", wildcardValues[deepKey]);

            ds.Unsubscribe(sub);
            ds.Unsubscribe(subWildcard);
        }

        [Fact]
        public async Task Test_WildcardDoesNotMatchAboveItsLevel()
        {
            var ds = new CacheWithWildcards<string, string?>();

            ConcurrentDictionary<string, string?> received = new();
            var sub = ds.Subscribe("a/b/#", async (s) =>
            {
                received[s.Key.ToString()] = s.Value;
            });

            // These should match
            await ds.PublishAsync("a/b/c", "value1", null, true);
            await ds.PublishAsync("a/b/c/d", "value2", null, true);

            // These should NOT match - publish to different branches to avoid conflicts
            await ds.PublishAsync("x", "value3", null, true);
            await ds.PublishAsync("x/y", "value4", null, true);
            await ds.PublishAsync("a/x", "value5", null, true);

            await Task.Delay(50);

            Assert.Equal(2, received.Count);
            Assert.Equal("value1", received["a/b/c"]);
            Assert.Equal("value2", received["a/b/c/d"]);

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_ConcurrentWildcardSubscriptions()
        {
            var config = new CacheConfig<string, string?> 
            { 
                MaxCallbackConcurrency = -1 
            };
            var ds = new CacheWithWildcards<string, string?>(config);
            //ds.WaitOnSubscriptionCallback = true;

            var tasks = new List<Task>();
            var subs = new ConcurrentBag<ISubscription<string, string?>>();
            var totalReceived = 0;

            // Create multiple wildcard subscriptions concurrently
            int max = 10;
            for (int i = 0; i < max; i++)
            {
                int index = i;
                tasks.Add(Task.Run(async () =>
                {
                    var sub = ds.Subscribe($"concurrent/{index}/#", async (s) =>
                    {
                        Interlocked.Increment(ref totalReceived);
                    });
                    subs.Add(sub);

                    await ds.PublishAsync($"concurrent/{index}/data", $"value{index}", null, true);
                }));
            }

            await Task.WhenAll(tasks);
            await Task.Delay(100);
            await TestHelper.WaitForValue<int>(max, () => totalReceived, 50);
            if (totalReceived < max)
            {
                Debug.WriteLine($"Expected at least {max} but got {totalReceived}");
            }

            // Each subscription should receive at least its own publish
            Assert.True(totalReceived >= max, $"Expected at least {max} but got {totalReceived}");
        }

        [Fact]
        public async Task Test_WildcardMatchingWithSpecialCharacters()
        {
            var ds = new CacheWithWildcards<string, string?>();

            ConcurrentDictionary<string, string?> received = new();
            var sub = ds.Subscribe("data/#", async (s) =>
            {
                received[s.Key.ToString()] = s.Value;
            });

            // Keys with numbers, underscores, hyphens
            await ds.PublishAsync("data/sensor_1", "value1", null, true);
            await ds.PublishAsync("data/device-2", "value2", null, true);
            await ds.PublishAsync("data/item123", "value3", null, true);

            await Task.Delay(50);

            Assert.Equal(3, received.Count);
            Assert.Equal("value1", received["data/sensor_1"]);
            Assert.Equal("value2", received["data/device-2"]);
            Assert.Equal("value3", received["data/item123"]);

            ds.Unsubscribe(sub);
        }

        [Fact]
        public async Task Test_SubscriptionCountTracking()
        {
            var ds = new CacheWithWildcards<string, string?>();

            Assert.Equal(0, ds.SubscribeCount);

            var sub1 = ds.Subscribe("a/b/c", async (s) => { });
            Assert.Equal(1, ds.SubscribeCount);

            var sub2 = ds.Subscribe("a/#", async (s) => { });
            Assert.Equal(2, ds.SubscribeCount);

            var sub3 = ds.Subscribe("a/b/#", async (s) => { });
            Assert.Equal(3, ds.SubscribeCount);

            ds.Unsubscribe(sub2);
            Assert.Equal(2, ds.SubscribeCount);

            ds.Unsubscribe(sub1);
            Assert.Equal(1, ds.SubscribeCount);

            ds.Unsubscribe(sub3);
            Assert.Equal(0, ds.SubscribeCount);
        }

        /// <summary>
        /// Data-driven test for FilterNode.Matches logic.
        /// 
        /// Tests wildcard pattern matching:
        /// - '#' matches one or more path levels (multi-level wildcard)
        /// - '+' matches exactly one path level (single-level wildcard)
        /// 
        /// TO ADD NEW TEST CASES:
        /// Simply add a new line to the FilterMatchTestCases() method below with:
        ///   new object[] { "wildcard/pattern", "topic/to/test", true_or_false }
        /// 
        /// Example:
        ///   new object[] { "sensors/+/temp", "sensors/room1/temp", true }
        /// </summary>
        [Theory]
        [MemberData(nameof(FilterMatchTestCases))]
        public async Task Test_FilterNodeMatches(string wildcardPattern, string testTopic, bool shouldMatch)
        {
            var ds = new CacheWithWildcards<string, string?>();
            //
            // subscribe both to test optic itself and wildcard...
            // -- this makes it run faster becuase we wait for the actual sub and then check if wildcard got it also
            //
            bool callbackInvoked = false;
            var sub = ds.Subscribe(testTopic, async (s) =>
            {
                if (s.Key.ToString() == testTopic)
                    callbackInvoked = true;
            });
            bool wcallbackInvoked = false;
            var subw = ds.Subscribe(wildcardPattern, async (s) =>
            {
                if (s.Key.ToString() == testTopic)
                    wcallbackInvoked = true;
            });

            await ds.PublishAsync(testTopic, "test-value", null, true);
            await TestHelper.WaitForValue<bool>(true, () => callbackInvoked, 20);
            Assert.True(callbackInvoked);

            if (shouldMatch != wcallbackInvoked)
            {
                Debug.WriteLine($"Failed... pattern:{wildcardPattern}, topic:{testTopic}, result:{wcallbackInvoked}");
            }
            Assert.True(shouldMatch == wcallbackInvoked, $"Failed... pattern:{wildcardPattern}, topic:{testTopic}, result:{wcallbackInvoked}");

            ds.Unsubscribe(sub);
            ds.Unsubscribe(subw);
        }

        /// <summary>
        /// Test cases for FilterNode.Matches testing.
        /// Format: wildcardPattern, testTopic, shouldMatch
        /// 
        /// NOTE: Patterns starting with wildcards (+/+, +/temp, #/temp, etc.) are not tested
        /// because they create tree nodes that conflict with direct topic publishes.
        /// </summary>
        public static IEnumerable<object[]> FilterMatchTestCases()
        {
            return new List<object[]>
            {
                // Multi-level wildcard (#) tests - matches one or more levels below parent
                new object[] { "a/#", "a/b", true },
                new object[] { "a/#", "a/b/c", true },
                new object[] { "a/#", "a/b/c/d", true },
                new object[] { "a/#", "a/b/c/d/e/f", true },
                new object[] { "a/#", "x/y", false },
                new object[] { "a/#", "b/a", false },

                // Single-level wildcard (+) tests - matches exactly one level
                new object[] { "a/+", "a/b", true },
                new object[] { "a/+", "a/x", true },
                new object[] { "a/+", "a/anything", true },
                new object[] { "a/+", "a/b/c", false },
                new object[] { "a/+", "x/y", false },

                // Mixed wildcards - single then specific
                new object[] { "a/+/c", "a/b/c", true },
                new object[] { "a/+/c", "a/x/c", true },
                new object[] { "a/+/c", "a/b/c/d", false },
                new object[] { "a/+/c", "a/b/x", false },
                new object[] { "a/+/c", "a/b", false },

                // Combination of + and #
                new object[] { "a/+/#", "a/b", false },
                new object[] { "a/+/#", "a/b/c", true },
                new object[] { "a/+/#", "a/b/c/d/e", true },
                new object[] { "a/+/#", "a/x/y/z", true },
                new object[] { "a/+/#", "x/b/c", false },

                // Root level wildcards
                new object[] { "#", "a", true },
                new object[] { "#", "a/b", true },
                new object[] { "#", "a/b/c", true },
                new object[] { "#", "x/y/z/deeply/nested", true },

                new object[] { "+", "a", true },
                new object[] { "+", "x", true },
                new object[] { "+", "a/b", false },

                // Exact matches within patterns
                new object[] { "home/+/temp", "home/kitchen/temp", true },
                new object[] { "home/+/temp", "home/bedroom/temp", true },
                new object[] { "home/+/temp", "home/livingroom/temp", true },
                new object[] { "home/+/temp", "home/kitchen/humidity", false },
                new object[] { "home/+/temp", "home/kitchen/temp/extra", false },
                new object[] { "home/+/temp", "office/kitchen/temp", false },

                // Complex patterns
                new object[] { "building/+/floor/+/#", "building/A/floor/1", false },
                new object[] { "building/+/floor/+/#", "building/A/floor/1/room/101", true },
                new object[] { "building/+/floor/+/#", "building/B/floor/2/room/202/desk", true },
                new object[] { "building/+/floor/+/#", "building/A/level/1/room/101", false },

                // Edge cases with special characters (valid path characters)
                new object[] { "data/+", "data/sensor_1", true },
                new object[] { "data/+", "data/device-2", true },
                new object[] { "data/+", "data/item123", true },

                // Multi-level patterns with exact matches
                new object[] { "a/b/+/d/#", "a/b/c/d", false },
                new object[] { "a/b/+/d/#", "a/b/c/d/e", true },
                new object[] { "a/b/+/d/#", "a/b/c/d/e/f/g", true },
                new object[] { "a/b/+/d/#", "a/b/x/d/y", true },
                new object[] { "a/b/+/d/#", "a/b/c/x/e", false },
                new object[] { "a/b/+/d/#", "a/b/c/c/d/e", false },

                // More comprehensive tests
                new object[] { "sensors/+/temperature", "sensors/room1/temperature", true },
                new object[] { "sensors/+/temperature", "sensors/outdoor/temperature", true },
                new object[] { "sensors/+/temperature", "sensors/room1/humidity", false },
                new object[] { "sensors/+/temperature", "sensors/room1/temperature/max", false },

                new object[] { "dev/+/+/status", "dev/server/instance1/status", true },
                new object[] { "dev/+/+/status", "dev/app/worker2/status", true },
                new object[] { "dev/+/+/status", "dev/server/status", false },
                new object[] { "dev/+/+/status", "prod/server/instance1/status", false },

                new object[] { "app/#", "app/module1", true },
                new object[] { "app/#", "app/module1/submodule", true },
                new object[] { "app/#", "app/module1/submodule/feature", true },
                new object[] { "app/#", "application/module1", false },
            };
        }

        [Theory]
        [MemberData(nameof(FilterMatchTestCases2))]
        public async Task Test_FilterNodeMatches2(string wildcardPattern, string testTopic, bool shouldMatch)
        {
            // for these tests filter MUST start the string
            Assert.True(wildcardPattern.StartsWith("#") || wildcardPattern.StartsWith("+"), $"Invalid test case - pattern must start with a valid segment: {wildcardPattern}");

            // create some dummy parameters
            string[] filter = wildcardPattern.Split('/');
            string partkey = filter[^1];
            var parent = new CacheWithWildcards<string, string?>.ItemNode("", "");
            // and a filternode using them
            var fn = new CacheWithWildcards<string, string?>.FilterNode(partkey, parent, filter);
            // and try match
            var res = fn.Matches(testTopic);
            if (shouldMatch != res)
            {
                Debug.WriteLine($"Failed... pattern:{wildcardPattern}, topic:{testTopic}, result:{res}");
            }
            Assert.True(shouldMatch == res, $"Failed... pattern:{wildcardPattern}, topic:{testTopic}, result:{res}");
            return;
        }

        /// <summary>
        /// Test cases for FilterNode.Matches testing.
        /// Format: wildcardPattern, testTopic, shouldMatch
        /// 
        /// These patterns MUST start with wildcard as they plug into the filter after parsing logic that creates tree nodes
        /// 
        /// </summary>
        public static IEnumerable<object[]> FilterMatchTestCases2()
        {
            return new List<object[]>
            {
                new object[] { "#", "a", true },
                new object[] { "#", "a/b", true },
                new object[] { "#", "a/b/c", true },
                new object[] { "#", "a/b/c/d", true },

                new object[] { "+/#", "a", false},
                new object[] { "+/#", "a/b", true },
                new object[] { "+/#", "a/b/c", true },
                new object[] { "+/#", "a/b/c/d", true },
            };
        }

        [Fact]
        public async Task Test_FilterNodeMatches3()
        {
            string wildcardPattern = "#";
            string testTopic = "a";

            // create some dummy parameters
            string[] filter = wildcardPattern.Split('/');
            string partkey = filter[^1];
            var parent = new CacheWithWildcards<string, string?>.ItemNode("", "");
            // and a filternode using them
            var fn = new CacheWithWildcards<string, string?>.FilterNode(partkey, parent, filter);
            // and try match
            var res = fn.Matches(testTopic);
            Assert.True(res, $"Failed... pattern:{wildcardPattern}, topic:{testTopic}, result:{res}");


            // match null string should throw
            Assert.Throws<ArgumentNullException>(() => fn.Matches(null!));

            // and parent path null internal validation should throw
            string path2 = null!;
            //var parent2 = new CacheWithPatterns<string, string?>.ItemNode("", path2);//
            // and a filternode using them
            var fn2 = new CacheWithWildcards<string, string?>.FilterNode(partkey, path2, filter);

            Assert.Throws<InvalidOperationException>(() => fn2.Matches("a"));

        }

    }
}
