using PSTT.Data;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Xunit.Sdk;

namespace PSTT.Data.Tests
{
    public class DataSourceTests
    {
        // Use the shared helper class
        private DataSourceTestHelper<string, string?> CreateHelper() => new();
        private DataSourceTestHelper<string, string?> CreateHelper(CacheConfig<string, string?> config) => new(config);

        [Fact]
        public async Task Test_SimpleSubPub()
        {
            // create a cache
            var cache = CreateHelper();
            Assert.True(cache.IsEmpty());

            var topic = "topic1";
            var dummy = "value1";
            var value = "";

            Assert.Null(cache.GetValue(topic));
            Assert.False(cache.TryGetValue(topic, out value));
            Assert.Equal(0, cache.Count);
            Assert.True(cache.IsEmpty());

            // subscribe to a topic - and save its value to a variable when it updates
            var sub1 = cache.Subscribe(topic, async s => { value = s.Value; });
            Assert.Equal(1, cache.Count);
            Assert.Equal(1, cache.SubscribeCount);
            Assert.False(cache.IsEmpty());
            // should be pending
            Assert.Equal(IStatus.StateValue.Pending, sub1.Status.State);
            Assert.True(sub1.Status.IsPending);
            // then update it
            await cache.PublishAsync(topic, dummy);
            // check that the subscriber got the update
            Assert.Equal(IStatus.StateValue.Active, sub1.Status.State);
            Assert.True(sub1.Status.IsActive);

            // give the async callback a chance to run and check value updated
            await cache.AssertValueAfterWait(dummy, () => value);

            Assert.Equal(0, ((Subscription<string, string>)sub1).InCallback);

            // drop request
            cache.Unsubscribe(sub1);
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.SubscribeCount);

        }

        [Fact]
        async public Task SomeOddStuff()
        {
            var cache = CreateHelper();

            // Install a handler that throws so we can test the DebugFail path in any build config.
            // The default handler wraps Debug.Fail which is a no-op in Release builds.
            var prevFail = Cache<string, string?>.DebugFail;
            var prevAssert = Cache<string, string?>.DebugAssert;
            Cache<string, string?>.DebugFail = msg => throw new InvalidOperationException(msg);
            Cache<string, string?>.DebugAssert = (cond, msg) => { if (!cond) throw new InvalidOperationException(msg); };
            try
            {
                Assert.Throws<InvalidOperationException>(() => Cache<string, string?>.DebugFail("dummy fail msg"));
                Cache<string, string?>.DebugAssert(true, "");  // should not throw
            }
            finally
            {
                Cache<string, string?>.DebugFail = prevFail;
                Cache<string, string?>.DebugAssert = prevAssert;
            }

            // WaitForValue with null value and null expected should return true immediately
            var (retry, ok) = await cache.WaitForValue<string?>(null, () => null, 3);
            Assert.True(ok);
            Assert.Equal(0, retry);
        }

        [Fact]
        public async Task Test_WaitForValue_ReturnsTrueForNullString()
        {
            var cache = CreateHelper();
            string? value = null;

            var (retry, ok) = await cache.WaitForValue<string?>(null, () => value, 3);
            Assert.True(ok);
            Assert.Equal(0, retry);
        }

        [Fact]
        public async Task Test_WaitForValue_WorksForValueTypes()
        {
            var cache = CreateHelper();
            var flag = false;

            var task = Task.Run(async () =>
            {
                await Task.Delay(2);
                flag = true;
            });

            var (retry, ok) = await cache.WaitForValue(true, () => flag, 20);
            await task;
            Assert.True(ok);
            Assert.InRange(retry, 0, 19);
        }

        [Fact]
        public async Task Test_WaitForValue_ReturnsRetryCountWhenNotMet()
        {
            var cache = CreateHelper();
            var actual = 0;

            var (retry, ok) = await cache.WaitForValue(123, () => actual, 2);
            Assert.False(ok);
            Assert.Equal(2, retry);
        }

        [Fact]
        public async Task Test_Unsubscribe_Twice_IsNoOp()
        {
            var cache = CreateHelper();
            var prevFail = Cache<string, string>.DebugFail;
            Cache<string, string>.DebugFail = _ => { }; // swallow fails in this test
            try
            {
                var callback = await TestHelper.CallbackThatFailsIfInvoked<string, string?>();

                var topic = "topic1";

                var sub = cache.Subscribe(topic, callback);
                Assert.Equal(1, cache.SubscribeCount);

                cache.Unsubscribe(sub);
                Assert.Equal(0, cache.SubscribeCount);

                cache.Unsubscribe(sub);
                Assert.Equal(0, cache.SubscribeCount);

                await cache.AssertEmptyAfterWait();
            }
            finally
            {
                Cache<string, string>.DebugFail = prevFail;
            }
        }

        [Fact]
        public async Task Test_RetainAcrossMultipleTopics_ThenDropOne()
        {
            var cache = CreateHelper();

            await cache.PublishAsync("a", "va", null, true);
            await cache.PublishAsync("b", "vb", null, true);
            await cache.PublishAsync("c", "vc", null, true);
            await cache.DoEventsAsync();

            Assert.Equal(3, cache.Count);
            Assert.Equal(0, cache.SubscribeCount);

            await cache.PublishAsync("b", "vb", null, false);
            await cache.DoEventsAsync();

            Assert.Equal(2, cache.Count);
            Assert.Null(cache.GetValue("b"));
            Assert.Equal("va", cache.GetValue("a"));
            Assert.Equal("vc", cache.GetValue("c"));
        }

        [Fact]
        public async Task Test_SubPub_WithBlckingInCallback()
        {
            // Use ThreadPool dispatcher WITH waitForCompletion for deterministic test execution
            // This test verifies that multiple subscriptions work correctly and callbacks complete
            var config = new CacheConfig<string, string?>
            {
                Dispatcher = new PSTT.Data.ThreadPoolDispatcher(waitForCompletion: true)
            };
            var cache = CreateHelper(config);
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.SubscribeCount);

            var topic = "topic1";
            var dummy = "dummyvalue";

            Assert.Null(cache.GetValue(topic));
            Assert.False(cache.TryGetValue(topic, out var value1));
            Assert.Equal(0, cache.Count);

            // subscribe to a topic - and save its value to a variable when it updates
            var sub1 = cache.Subscribe(topic, async s => { value1 = s.Value; });
            Assert.Equal(1, cache.Count);
            Assert.Equal(1, cache.SubscribeCount);
            Assert.Equal(0, ((Subscription<string, string>)sub1).InCallback);
            // should be pending
            Assert.Equal(IStatus.StateValue.Pending, sub1.Status.State);
            Assert.True(sub1.Status.IsPending);
            // then update it
            await cache.PublishAsync(topic, dummy);

            // check that the subscriber got the update
            Assert.Equal(IStatus.StateValue.Active, sub1.Status.State);
            Assert.True(sub1.Status.IsActive);

            // With waitForCompletion=true, callbacks complete before PublishAsync returns
            Assert.Equal(dummy, value1);

            // and check static read value
            Assert.Equal(dummy, cache.GetValue(topic));
            Assert.True(cache.TryGetValue(topic, out var value2));
            Assert.Equal(dummy, value2);

            // 2nd sub - test that subscribing to existing value immediately activates
            value2 = null; // reset
            var sub2 = cache.Subscribe(topic, async s =>
            {
                value2 = s.Value;
            });
            Assert.Equal(1, cache.Count);
            Assert.Equal(2, cache.SubscribeCount);
            Assert.Equal(2, cache.SubscriptionCount);
            Assert.True(sub2.Status.IsActive);
            Assert.Equal(dummy, sub2.Value);

            // With waitForCompletion=true, subscription callback completes immediately
            Assert.Equal(dummy, value2);
            Assert.Equal(0, ((Subscription<string, string>)sub2!).InCallback);

            // 2nd pub to same sub with different value
            var dummy2 = dummy + "2";
            await cache.PublishAsync(topic, dummy2);
            Assert.Equal(dummy2, sub1.Value);
            Assert.Equal(dummy2, sub2.Value);

            // Callbacks complete before PublishAsync returns
            Assert.Equal(dummy2, value1);
            Assert.Equal(dummy2, value2);

            // 3rd publish
            var dummy3 = dummy + "3";
            await cache.PublishAsync(topic, dummy3);
            Assert.Equal(dummy3, sub1.Value);
            Assert.Equal(dummy3, sub2.Value);
            Assert.Equal(dummy3, value1);
            Assert.Equal(dummy3, value2);
            Assert.Equal(0, ((Subscription<string, string>)sub2).InCallback);

            // overall cache not busy either
            Assert.Equal(0, cache.InCallback);
            Assert.Equal(0, cache.ActiveCallbacks);

            // drop request #1
            cache.Unsubscribe(sub1);
            Assert.Equal(1, cache.Count);
            Assert.Equal(1, cache.SubscriptionCount);

            // drop request #2
            cache.Unsubscribe(sub2);
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.SubscribeCount);
            Assert.Equal(0, cache.SubscriptionCount);

            Assert.Equal(0, cache.InCallback);
            Assert.Equal(0, cache.ActiveCallbacks);

        }

        [Fact]
        public async Task Test_SubPub_Stale()
        {
            // create a cache
            var cache = CreateHelper();
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.SubscribeCount);

            var topic = "topic1";
            var dummy = "dummyvalue";
            var value1 = "";
            var count = 0;
            Status? cbstatus = null;

            // subscribe to a topic - and save its value to a variable when it updates
            var sub1 = cache.Subscribe(topic, async s => { count++; cbstatus = (Status)s.Status; value1 = s.Value; });
            await cache.DoEventsAsync(); // give the async callback a chance to run

            var reason = "stale reason";
            Status status = new Status() { State = IStatus.StateValue.Stale, Message = reason, Code = 0 };
            await cache.PublishAsync(topic, dummy, status);

            //await cache.DoEventsAsync(); // give the async callback a chance to run
            await cache.AssertValueAfterWait(true, () => count > 0);
            Assert.Equal(dummy, sub1.Value);
            Assert.True(sub1.Status.IsStale);
            Assert.Equal(reason, sub1.Status.Message);

            // and check still in cache...
            Assert.Equal(1, cache.Count);
            Assert.Equal(1, cache.SubscribeCount);
            Assert.Equal(0, cache.InCallback);

            // and update value to active again
            var dummy2 = dummy + "2";
            await cache.PublishAsync(topic, dummy2);
            await cache.DoEventsAsync(); // give the async callback a chance to run

            Assert.Equal(dummy2, sub1.Value);
            Assert.True(sub1.Status.IsActive);
            Assert.False(sub1.Status.IsStale);
            Assert.Null(sub1.Status.Message);

            // and check still in cache...
            Assert.Equal(1, cache.Count);
            Assert.Equal(1, cache.SubscribeCount);
            Assert.Equal(0, cache.InCallback);


        }
        [Fact]
        public async Task Test_SubPub_Fail()
        {
            // create a cache
            var cache = new Cache<string, string>();

            var topic = "topic1";
            var dummy = "dummyvalue";
            var value1 = "";

            // subscribe to a topic - and save its value to a variable when it updates
            var sub1 = cache.Subscribe(topic, async s => { value1 = s.Value; });
            await cache.DoEventsAsync(); // give the async callback a chance to run

            var reason = "fail reason";
            var status = new Status() { State = IStatus.StateValue.Failed, Message = reason, Code = 0 };
            await cache.PublishAsync(topic, dummy, status);
            await cache.DoEventsAsync(); // give the async callback a chance to run

            Assert.Equal(dummy, sub1.Value);
            Assert.True(sub1.Status.IsFailed);
            Assert.Equal(reason, sub1.Status.Message);

            // and check not still in cache...
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.SubscribeCount);
            Assert.Equal(0, cache.InCallback);

        }

        /// <summary>
        /// Publish value with no subcribers and retain set - stays in cache
        /// Subscribe to that value, get that value - one item in cache
        /// Unsubsribe and items remains in cache
        /// Publish value with retain false - item dropped ,nothing in cache
        /// </summary>
        /// <returns></returns>
        [Fact]
        public async Task Test_PubRetainThenDrop()
        {
            // create a cache
            var cache = CreateHelper();

            // push value
            var topic = "topic1";
            var dummy = "value1";
            await cache.PublishAsync(topic, dummy, null, true);
            await cache.DoEventsAsync(); // give the async callback a chance to run
            Assert.Equal(1, cache.Count);
            Assert.Equal(0, cache.SubscribeCount);

            // subscribe to topic - and save its value to a variable when it updates
            var value = "";
            var sub1 = cache.Subscribe(topic, async s => { value = s.Value; });
            Assert.Equal(1, cache.Count);
            Assert.Equal(1, cache.SubscribeCount);
            // status is inline read and not from callback so should be active immediately
            Assert.True(sub1.Status.IsActive);
            // check that the subscriber got the update
            await cache.WaitForValue(dummy, () => value, 5);
            Assert.Equal(dummy, value);

            // drop request
            cache.Unsubscribe(sub1);
            Assert.Equal(1, cache.Count);
            Assert.Equal(0, cache.SubscribeCount);

            // push value and clear retain flag and it should get dropped
            await cache.PublishAsync(topic, dummy, null, false);
            await cache.DoEventsAsync(); // give the async callback a chance to run
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.SubscribeCount);
        }

        /// <summary>
        /// Publish value with no subcribers and retain set - stays in cache
        /// Subscribe to that value, get that value - one item in cache
        /// Publish value with retain false - item still in cache and gets update
        /// Unsubsribe and cache now empty
        /// </summary>
        /// <returns></returns>
        [Fact]
        public async Task Test_PubRetainThenClearRetainWhilstSubscribed()
        {
            // create a cache
            var cache = CreateHelper();

            // push value
            var topic = "topic1";
            var dummy = "value1";
            await cache.PublishAsync(topic, dummy, null, true);
            await cache.DoEventsAsync(); // give the async callback a chance to run
            Assert.Equal(1, cache.Count);
            Assert.Equal(0, cache.SubscribeCount);

            // subscribe to topic - and save its value to a variable when it updates
            var value = "";
            var sub1 = cache.Subscribe(topic, async s => { value = s.Value; });
            Assert.Equal(1, cache.Count);
            Assert.Equal(1, cache.SubscribeCount);
            // status is inline read and not from callback so should be active immediately
            Assert.True(sub1.Status.IsActive);
            // check that the subscriber got the update
            await cache.AssertValueAfterWait(dummy, () => value);

            // push value and clear retain flag and it should get dropped
            var dummy2 = dummy + "2";
            await cache.PublishAsync(topic, dummy2, null, false);
            Assert.Equal(1, cache.Count);
            Assert.Equal(1, cache.SubscribeCount);
            await cache.AssertValueAfterWait(dummy2, () => value);

            // drop request
            cache.Unsubscribe(sub1);
            await cache.AssertEmptyAfterWait();

        }

        /// <summary>
        /// Publish value with no subscribers - nothing happens and droppped immediately, cache empty
        /// Then publish no retain/fail status - nothing happens and droppped immediately, cache empty
        /// </summary>
        /// <returns></returns>
        [Fact]
        public async Task Test_PubNoRetainDrop()
        {
            // create a cache
            var cache = CreateHelper();

            // push value without retain and no-one is listening
            // ...drops immediately due to HandleFail whcih checks for no subscribers and no retain
            // ...in some future with lazy caching it may hang around?
            var topic = "topic1";
            var dummy = "value1";
            await cache.PublishAsync(topic, dummy, null, false);
            await cache.AssertEmptyAfterWait();

            // and try it with a status
            Status failStatus = new Status() { State = IStatus.StateValue.Failed, Message = "fail reason", Code = 0 };
            await cache.PublishAsync(topic, dummy, failStatus, false);
            await cache.AssertEmptyAfterWait();

        }

        [Fact]
        public async Task Test_ConcurrentSubsAndPubs()
        {
            foreach (var con in new[] { 20, 10, 5, 1 })
            {
                // create a cache with specified max callback concurrency and ThreadPool dispatcher that waits
                // With waitForCompletion=true, all callbacks complete before PublishAsync returns
                // This tests that concurrency limiting works correctly without callback skipping
                var config = new CacheConfig<string, string?>
                {
                    MaxCallbackConcurrency = con,
                    Dispatcher = new PSTT.Data.ThreadPoolDispatcher(waitForCompletion: true)
                };
                var cache = CreateHelper(config);

                var topic = "topic1";
                int subCount = 100;
                int pubCount = 100;
                int totalCount = subCount * pubCount;
                var valuesReceived = new ConcurrentBag<string?>();

                // create a bunch of subs
                var subs = new List<ISubscription<string, string?>>();
                for (int i = 0; i < subCount; i++)
                {
                    var sub = cache.Subscribe(topic, async s =>
                    {
                        valuesReceived.Add(s.Value);
                        // No delay - with waitForCompletion=true, callbacks complete before PublishAsync returns
                    });
                    subs.Add(sub);
                }

                // publish a bunch of times concurrently
                var publishTasks = new List<Task>();
                for (int i = 0; i < pubCount; i++)
                {
                    int pubIndex = i; // capture loop variable
                    publishTasks.Add(Task.Run(async () =>
                    {
                        await cache.PublishAsync(topic, $"value{pubIndex}");
                    }));
                }
                await Task.WhenAll(publishTasks);

                // With waitForCompletion=true, PublishAsync waits for callbacks to complete
                // However, when MaxCallbackConcurrency is reached, newer callbacks may still be skipped
                // The total should equal received + skipped
                Assert.Equal(totalCount, valuesReceived.Count + cache.SkippedCallback);

                // With concurrency=20 (first iteration), we expect most/all callbacks to execute
                if (con == 20)
                {
                    Assert.True(valuesReceived.Count >= totalCount * 0.95,
                        $"Expected most callbacks with high concurrency. Got {valuesReceived.Count}/{totalCount}");
                }
            }
        }

        [Fact]
        public async Task Test_SubscribeToNonExistingTopic_AndThenUunsubscribe()
        {
            // create a cache
            var cache = CreateHelper();
            var topic = "nonExistingTopic";
            var callback = await CallbackThatFailsIfInvoked();
            var sub = cache.Subscribe(topic, callback);
            Assert.Equal(1, cache.Count);
            Assert.Equal(1, cache.SubscribeCount);
            Assert.True(sub.Status.IsPending);
            Assert.Null(cache.GetValue(topic));

            // unsubscribe and check cache is empty
            cache.Unsubscribe(sub);
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.SubscribeCount);

        }

        [Fact]
        public async Task Test_UsingNullKey()
        {
            // create a cache
            var cache = CreateHelper();
            string topic = null!;

            var callback = await CallbackThatFailsIfInvoked();

            // Subscribe should throw when provided a null key
            _ = Assert.Throws<ArgumentNullException>(() => cache.Subscribe(topic, callback));

            Assert.True(cache.IsEmpty());

            // Pub  should throw when provided a null key
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.PublishAsync(topic, "somevalue"));

            Assert.True(cache.IsEmpty());
        }

        // create a callback that will fail if called

        async Task<Func<ISubscription<string, string?>, Task>> CallbackThatFailsIfInvoked()
        {
            Func<ISubscription<string, string?>, Task> callback = async s => { throw new Exception("Callback was called!!"); };
            // and test it fails (also helps with code coverage!)...
            await Assert.ThrowsAsync<Exception>(() => callback(null!));
            return callback;
        }

        [Fact]
        public async Task Test_MaxTopics_LimitExceeded()
        {
            var config = new CacheConfig<string, string?> { MaxTopics = 1 };
            var cache = CreateHelper(config);

            var callback = await CallbackThatFailsIfInvoked();

            // first subscribe should succeed
            var sub1 = cache.Subscribe("a", callback);
            Assert.NotNull(sub1);

            // second distinct topic should cause limit exceeded
            var ex = Assert.Throws<InvalidOperationException>(() => cache.Subscribe("b", callback));
            Assert.NotNull(ex);
        }

        [Fact]
        public async Task Test_MaxSubscriptionsPerTopic_LimitExceeded()
        {
            var config = new CacheConfig<string, string?> { MaxSubscriptionsPerTopic = 2 };
            var cache = CreateHelper(config);

            // create a callback that will fail if called
            Func<ISubscription<string, string?>, Task> callback = async s => { throw new Exception("Callback was called!!"); };
            // and test it fails (also helps with code coverage!)...
            await Assert.ThrowsAsync<Exception>(() => callback(null!));

            // allow a couple of subscriptions until the per-topic limit is exceeded
            var sub1 = cache.Subscribe("topic1", callback);
            var sub2 = cache.Subscribe("topic1", callback);
            Assert.Equal(1, cache.Count);
            Assert.Equal(2, cache.SubscribeCount);

            // next subscription on same topic should exceed the per-topic limit
            var ex1 = Assert.Throws<InvalidOperationException>(() => cache.Subscribe("topic1", callback));
            Assert.NotNull(ex1);
            Assert.Equal(1, cache.Count);
            Assert.Equal(2, cache.SubscribeCount);

            // and again
            var ex2 = Assert.Throws<InvalidOperationException>(() => cache.Subscribe("topic1", callback));
            Assert.NotNull(ex2);
            Assert.Equal(1, cache.Count);
            Assert.Equal(2, cache.SubscribeCount);

            // then drop and subscribe again should work as we're back under the limit
            cache.Unsubscribe(sub1);
            Assert.Equal(1, cache.Count);
            Assert.Equal(1, cache.SubscribeCount);

            sub1 = cache.Subscribe("topic1", callback);
            Assert.Equal(1, cache.Count);
            Assert.Equal(2, cache.SubscribeCount);

            // and finally throws again
            var ex3 = Assert.Throws<InvalidOperationException>(() => cache.Subscribe("topic1", callback));
            Assert.NotNull(ex3);
        }

        [Fact]
        public async Task Test_MaxSubscriptionsTotal_LimitExceeded()
        {
            var config = new CacheConfig<string, string?> { MaxSubscriptionsTotal = 2 };
            var cache = CreateHelper(config);

            // create a callback that will fail if called
            Func<ISubscription<string, string?>, Task> callback = async s => { throw new Exception("Callback was called!!"); };
            // and test it fails (also helps with code coverage!)...
            await Assert.ThrowsAsync<Exception>(() => callback(null!));

            // MaxSubscriptionsTotal=2 means we can have up to 2 total subscriptions
            var sub1 = cache.Subscribe("a", callback);
            var sub2 = cache.Subscribe("b", callback);
            Assert.Equal(2, cache.SubscribeCount);

            // Third subscription should exceed the limit
            var ex = Assert.Throws<InvalidOperationException>(() => cache.Subscribe("c", callback));
            Assert.Equal(2, cache.SubscribeCount);
        }

        [Fact]
        public async Task Test_CallbackThrowsException()
        {
            // Use synchronous dispatcher for deterministic exception handling
            var config = new CacheConfig<string, string?>
            {
                Dispatcher = new PSTT.Data.SynchronousDispatcher()
            };
            var cache = CreateHelper(config);
            var topic = "topic1";
            var called = 0;
            var sub = cache.Subscribe(topic, async s => { called++; throw new Exception("Callback exception"); });
            await cache.PublishAsync(topic, "value1");

            // With synchronous dispatcher, callback completes before PublishAsync returns
            Assert.Equal(1, called);

            // if we get here, the exception was caught and did not crash the test runner
            Assert.Equal(0, cache.InCallback);

            Assert.Equal(1, cache.SubscriptionCount);
            Assert.Equal(1, cache.Count);
            Assert.True(sub.Status.IsActive);
            Assert.Equal(1, cache.ExceptionInCallback);
        }
        [Fact]
        public async Task Test_PublishStatusToNonExistingSub()
        {
            // Use synchronous dispatcher for deterministic exception handling
            var config = new CacheConfig<string, string?>
            {
                Dispatcher = new PSTT.Data.SynchronousDispatcher()
            };
            var cache = CreateHelper(config);
            var topic = "topic1";

            var statusStale = new Status() { State = IStatus.StateValue.Stale, Message = "stale reason", Code = 0 };
            var statusFail = new Status() { State = IStatus.StateValue.Failed, Message = "failed reason", Code = 0 };

            await cache.PublishAsync(topic, "value1", statusStale);

            // no retain...so shoudl just come and go...
            Assert.True(cache.IsEmpty());

            // then with retain...
            await cache.PublishAsync(topic, "value1", statusStale, true);

            // ...so shoudl stay
            Assert.False(cache.IsEmpty());
            Assert.Equal(1, cache.Count);

            // then fail and shoudl disappear even with retain...
            await cache.PublishAsync(topic, "value1", statusFail, true);
            Assert.True(cache.IsEmpty());

        }

        [Fact]
        public async Task Test_CreateDataSurceWIthNullConfig()
        {
            // Should throw
            Assert.Throws<ArgumentNullException>(() => CreateHelper(null!));
        }
    }
}
