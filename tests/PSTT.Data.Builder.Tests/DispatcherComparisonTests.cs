using PSTT.Data;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Linq;

namespace PSTT.Data.Tests
{
    /// <summary>
    /// Tests that compare behavior between different dispatcher implementations.
    /// These tests serve as documentation of dispatcher differences.
    /// </summary>
    public class DispatcherComparisonTests
    {
        [Fact]
        public async Task Compare_Synchronous_vs_ThreadPool()
        {
            var results = new ConcurrentDictionary<string, (long elapsedMs, string value)>();

            // Test synchronous dispatcher
            var syncDataSource = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .Build();

            string? syncValue = null;
            var syncSub = syncDataSource.Subscribe("test", async (s) =>
            {
                await Task.Delay(20); // Simulate work
                syncValue = s.Value;
            });

            var syncWatch = Stopwatch.StartNew();
            await syncDataSource.PublishAsync("test", "syncValue");
            syncWatch.Stop();

            results["synchronous"] = (syncWatch.ElapsedMilliseconds, syncValue!);

            // Test thread pool dispatcher (no wait) — gate ensures assertion is not timing-dependent
            var asyncDataSource = new CacheBuilder<string, string>()
                .WithThreadPoolCallbacks(waitForCompletion: false)
                .Build();

            string? asyncValue = null;
            var asyncStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var asyncGate    = new SemaphoreSlim(0, 1);

            var asyncSub = asyncDataSource.Subscribe("test", async (s) =>
            {
                asyncStarted.TrySetResult(true); // signal: callback has fired
                await asyncGate.WaitAsync();     // block until test releases
                asyncValue = s.Value;
            });

            await asyncDataSource.PublishAsync("test", "asyncValue");

            // Wait until the fire-and-forget callback has actually started
            await asyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Async no-wait: PublishAsync returned before callback body completed
            Assert.Null(asyncValue);

            // Release the callback and wait for it to finish
            asyncGate.Release();
            await Task.Delay(50);

            results["async_no_wait"] = (0, asyncValue!);

            // Synchronous should take longer (waited for callback)
            Assert.True(results["synchronous"].elapsedMs >= 15,
                "Synchronous dispatcher should wait for callback");

            // Async no-wait should have returned before callback finished
            Assert.Equal("asyncValue", results["async_no_wait"].value);

            // Both should have received values
            Assert.Equal("syncValue", results["synchronous"].value);
        }

        [Fact]
        public async Task Compare_ThreadPool_Wait_vs_NoWait()
        {
            var callbackExecuted = new ConcurrentBag<string>();

            // Test with wait
            var withWaitDataSource = new CacheBuilder<string, string>()
                .WithThreadPoolCallbacks(waitForCompletion: true)
                .Build();

            var sub1 = withWaitDataSource.Subscribe("test", async (s) =>
            {
                await Task.Delay(10);
                callbackExecuted.Add("with_wait");
            });

            var withWaitWatch = Stopwatch.StartNew();
            await withWaitDataSource.PublishAsync("test", "value1");
            withWaitWatch.Stop();

            // Test without wait — use a semaphore so the assertion is not timing-dependent.
            // The callback is held at a gate until after we've asserted it hasn't completed yet.
            var noWaitDataSource = new CacheBuilder<string, string>()
                .WithThreadPoolCallbacks(waitForCompletion: false)
                .Build();

            var callbackStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbackGate    = new SemaphoreSlim(0, 1);

            var sub2 = noWaitDataSource.Subscribe("test", async (s) =>
            {
                callbackStarted.TrySetResult(true); // signal: callback has fired
                await callbackGate.WaitAsync();     // block until test releases
                callbackExecuted.Add("no_wait");
            });

            await noWaitDataSource.PublishAsync("test", "value1");

            // Wait until the fire-and-forget callback has actually started (proves it fired)
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // no-wait: PublishAsync returned before the callback body completed
            Assert.DoesNotContain("no_wait", callbackExecuted);

            // Release the blocked callback and wait for it to finish
            callbackGate.Release();
            await Task.Delay(50);

            Assert.True(withWaitWatch.ElapsedMilliseconds >= 10,
                "With wait should block until callback completes");

            Assert.Contains("with_wait", callbackExecuted);
            Assert.Contains("no_wait", callbackExecuted);
        }

        [Fact]
        public async Task Compare_ConcurrencyLimits_WithDifferentDispatchers()
        {
            var syncConcurrency = new ConcurrentBag<int>();
            var asyncConcurrency = new ConcurrentBag<int>();

            // Synchronous with concurrency limit
            var syncDataSource = new CacheBuilder<string, string>()
                .WithMaxCallbackConcurrency(2)
                .WithSynchronousCallbacks()
                .Build();

            var syncSub = syncDataSource.Subscribe("test", async (s) =>
            {
                var current = Interlocked.Increment(ref syncConcurrentCount);
                syncConcurrency.Add(current);
                await Task.Delay(10);
                Interlocked.Decrement(ref syncConcurrentCount);
            });

            // Async (fire-and-forget) with concurrency limit
            var asyncDataSource = new CacheBuilder<string, string>()
                .WithMaxCallbackConcurrency(2)
                .WithThreadPoolCallbacks(waitForCompletion: false)
                .Build();

            var asyncSub = asyncDataSource.Subscribe("test", async (s) =>
            {
                var current = Interlocked.Increment(ref asyncConcurrentCount);
                asyncConcurrency.Add(current);
                await Task.Delay(10);
                Interlocked.Decrement(ref asyncConcurrentCount);
            });

            // Fire multiple updates rapidly
            for (int i = 0; i < 10; i++)
            {
                _ = syncDataSource.PublishAsync("test", $"value{i}");
                _ = asyncDataSource.PublishAsync("test", $"value{i}");
            }

            await Task.Delay(200); // Wait for all callbacks

            // Both dispatchers now enforce MaxCallbackConcurrency via the ActiveCallbacks counter,
            // which tracks actual callback body executions (including fire-and-forget ones).
            Assert.All(syncConcurrency, count => Assert.True(count <= 2,
                "Sync dispatcher should respect concurrency limit"));

            Assert.All(asyncConcurrency, count => Assert.True(count <= 2,
                "Fire-and-forget dispatcher should also respect concurrency limit via ActiveCallbacks"));
        }

        private int syncConcurrentCount = 0;
        private int asyncConcurrentCount = 0;

        [Fact]
        public void Demonstrate_DispatcherNames()
        {
            // Example: How to check which dispatcher is being used
            ICallbackDispatcher[] dispatchers = new ICallbackDispatcher[]
            {
                new SynchronousDispatcher(),
                new ThreadPoolDispatcher(waitForCompletion: true),
                new ThreadPoolDispatcher(waitForCompletion: false)
            };

            var names = dispatchers.Select(d => d.Name).ToArray();

            Assert.Contains(names, n => n == "Synchronous");
            Assert.Contains(names, n => n == "ThreadPool(wait)");
            Assert.Contains(names, n => n == "ThreadPool(fire-and-forget)");
        }
    }
}
