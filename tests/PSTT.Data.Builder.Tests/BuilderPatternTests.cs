using PSTT.Data;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace PSTT.Data.Tests
{
    /// <summary>
    /// Tests demonstrating the builder pattern and various configuration options.
    /// These tests also serve as example code for users of the library.
    /// </summary>
    public class BuilderPatternTests
    {
        [Fact]
        public void Test_Builder_DefaultConfiguration()
        {
            // Example: Creating a DataSource with default configuration
            var dataSource = new CacheBuilder<string, string>()
                .Build();

            Assert.NotNull(dataSource);
            Assert.Equal(2, dataSource.MaxCallbackConcurrency);
            Assert.Equal(1000, dataSource.MaxTopics);
            Assert.Equal(100, dataSource.MaxSubscriptionsPerTopic);
            Assert.Equal(10000, dataSource.MaxSubscriptionsTotal);
        }

        [Fact]
        public void Test_Builder_CustomLimits()
        {
            // Example: Creating a DataSource with custom limits
            var dataSource = new CacheBuilder<string, int>()
                .WithMaxTopics(500)
                .WithMaxSubscriptionsPerTopic(50)
                .WithMaxSubscriptionsTotal(5000)
                .WithMaxCallbackConcurrency(5)
                .Build();

            Assert.Equal(500, dataSource.MaxTopics);
            Assert.Equal(50, dataSource.MaxSubscriptionsPerTopic);
            Assert.Equal(5000, dataSource.MaxSubscriptionsTotal);
            Assert.Equal(5, dataSource.MaxCallbackConcurrency);
        }

        [Fact]
        public async Task Test_Builder_SynchronousDispatcher()
        {
            // Example: Creating a DataSource with synchronous callbacks (blocking)
            var dataSource = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .Build();

            string? receivedValue = null;
            var sub = dataSource.Subscribe("test", async (s) =>
            {
                receivedValue = s.Value;
                await Task.Delay(10); // Simulate work
            });

            var stopwatch = Stopwatch.StartNew();
            await dataSource.PublishAsync("test", "value1");
            stopwatch.Stop();

            // With synchronous dispatcher, publish waits for callback
            Assert.Equal("value1", receivedValue);
            Assert.True(stopwatch.ElapsedMilliseconds >= 10, "Should have waited for callback");
        }

        [Fact]
        public async Task Test_Builder_ThreadPoolDispatcher_NoWait()
        {
            // Example: Creating a DataSource with fire-and-forget thread pool callbacks
            var dataSource = new CacheBuilder<string, string>()
                .WithThreadPoolCallbacks(waitForCompletion: false)
                .Build();

            string? receivedValue = null;
            var sub = dataSource.Subscribe("test", async (s) =>
            {
                await Task.Delay(200); // Simulate slow work
                receivedValue = s.Value;
            });

            await dataSource.PublishAsync("test", "value1");

            // With fire-and-forget, publish returns before the slow callback completes
            Assert.Null(receivedValue);

            // Wait for callback to complete
            await Task.Delay(300);
            Assert.Equal("value1", receivedValue);
        }

        [Fact]
        public async Task Test_Builder_ThreadPoolDispatcher_WithWait()
        {
            // Example: Creating a DataSource with thread pool callbacks that wait
            var dataSource = new CacheBuilder<string, string>()
                .WithThreadPoolCallbacks(waitForCompletion: true)
                .Build();

            string? receivedValue = null;
            var sub = dataSource.Subscribe("test", async (s) =>
            {
                await Task.Delay(10); // Simulate work
                receivedValue = s.Value;
            });

            var stopwatch = Stopwatch.StartNew();
            await dataSource.PublishAsync("test", "value1");
            stopwatch.Stop();

            // With wait, publish waits for callback
            Assert.Equal("value1", receivedValue);
            Assert.True(stopwatch.ElapsedMilliseconds >= 10, "Should have waited for callback");
        }

        [Fact]
        public async Task Test_Builder_CustomDispatcher()
        {
            // Example: Creating a DataSource with a custom dispatcher
            var customDispatcher = new CustomLoggingDispatcher();
            
            var dataSource = new CacheBuilder<string, string>()
                .WithDispatcher(customDispatcher)
                .Build();

            string? receivedValue = null;
            var sub = dataSource.Subscribe("test", async (s) =>
            {
                receivedValue = s.Value;
            });

            await dataSource.PublishAsync("test", "value1");
            await Task.Delay(10); // Allow async dispatch

            Assert.Equal("value1", receivedValue);
            Assert.True(customDispatcher.CallbacksDispatched > 0, "Custom dispatcher should have been used");
        }

        [Fact]
        public async Task Test_Builder_ErrorHandler()
        {
            // Example: Creating a DataSource with error handling
            var exceptions = new ConcurrentBag<Exception>();
            var errorMessages = new ConcurrentBag<string>();

            // Use thread pool with wait to ensure error handler executes
            var dataSource = new CacheBuilder<string, string>()
                .WithThreadPoolCallbacks(waitForCompletion: true)
                .WithCallbackErrorHandler((ex, msg) =>
                {
                    exceptions.Add(ex);
                    errorMessages.Add(msg);
                })
                .Build();

            var sub = dataSource.Subscribe("test", async (s) =>
            {
                throw new InvalidOperationException("Test exception in callback");
            });

            // With waitForCompletion=true, this will wait for callback (and error handler)
            await dataSource.PublishAsync("test", "value1");

            Assert.Single(exceptions);
            Assert.Contains("Test exception in callback", exceptions.First().Message);
            Assert.Single(errorMessages);
            Assert.Contains("test", errorMessages.First());
        }

        [Fact]
        public async Task Test_Builder_CombinedConfiguration()
        {
            // Example: Creating a fully configured DataSource
            var errors = new List<string>();
            
            var dataSource = new CacheBuilder<string, int>()
                .WithMaxTopics(100)
                .WithMaxSubscriptionsPerTopic(10)
                .WithMaxCallbackConcurrency(3)
                .WithThreadPoolCallbacks(waitForCompletion: false)
                .WithCallbackErrorHandler((ex, msg) => errors.Add($"{msg}: {ex.Message}"))
                .Build();

            Assert.Equal(100, dataSource.MaxTopics);
            Assert.Equal(10, dataSource.MaxSubscriptionsPerTopic);
            Assert.Equal(3, dataSource.MaxCallbackConcurrency);

            // Test it works
            int receivedValue = 0;
            var sub = dataSource.Subscribe("counter", async (s) =>
            {
                receivedValue = s.Value;
            });

            await dataSource.PublishAsync("counter", 42);
            await Task.Delay(20);

            Assert.Equal(42, receivedValue);
        }

        [Fact]
        public async Task Test_RealWorld_HighThroughputScenario()
        {
            // Example: High-throughput scenario with fire-and-forget callbacks
            var dataSource = new CacheBuilder<string, int>()
                .WithMaxTopics(1000)
                .WithThreadPoolCallbacks(waitForCompletion: false)
                .WithMaxCallbackConcurrency(10)
                .Build();

            var received = new ConcurrentDictionary<string, List<int>>();
            var topics = new[] { "sensor1", "sensor2", "sensor3", "sensor4", "sensor5" };

            // Subscribe to multiple topics
            foreach (var topic in topics)
            {
                dataSource.Subscribe(topic, async (s) =>
                {
                    var list = received.GetOrAdd(s.Key, _ => new List<int>());
                    lock (list)
                    {
                        list.Add(s.Value);
                    }
                });
            }

            // Publish many values rapidly
            var tasks = new List<Task>();
            for (int i = 0; i < 100; i++)
            {
                foreach (var topic in topics)
                {
                    tasks.Add(dataSource.PublishAsync(topic, i));
                }
            }

            await Task.WhenAll(tasks);
            await Task.Delay(100); // Allow callbacks to complete

            // Verify all topics received values
            Assert.Equal(topics.Length, received.Count);
            foreach (var topic in topics)
            {
                Assert.True(received[topic].Count > 0, $"Topic {topic} should have received values");
            }
        }

        [Fact]
        public async Task Test_RealWorld_RetainedValues()
        {
            // Example: Using retained values for "last will" or configuration
            var dataSource = new CacheBuilder<string, string>()
                .Build();

            // Publish some configuration values with retain=true
            await dataSource.PublishAsync("config/server", "https://api.example.com", null, retain: true);
            await dataSource.PublishAsync("config/timeout", "30", null, retain: true);

            Assert.Equal(2, dataSource.Count);
            Assert.Equal(0, dataSource.SubscribeCount); // No subscribers yet

            // Later, new subscriber gets retained values immediately
            string? serverValue = null;
            string? timeoutValue = null;

            var sub1 = dataSource.Subscribe("config/server", async (s) => { serverValue = s.Value; });
            var sub2 = dataSource.Subscribe("config/timeout", async (s) => { timeoutValue = s.Value; });

            await Task.Delay(10);

            Assert.Equal("https://api.example.com", serverValue);
            Assert.Equal("30", timeoutValue);
        }

        /// <summary>
        /// Custom dispatcher example that logs all callbacks
        /// </summary>
        private class CustomLoggingDispatcher : ICallbackDispatcher
        {
            public int CallbacksDispatched { get; private set; }

            public string Name => "CustomLogging";

            public bool WaitsForCompletion => false;

            public async Task DispatchAsync(Func<Task> callback, CancellationToken cancellationToken = default)
            {
                CallbacksDispatched++;
                Debug.WriteLine($"[CustomLoggingDispatcher] Dispatching callback #{CallbacksDispatched}");
                _ = Task.Run(callback, cancellationToken); // Fire and forget
                await Task.CompletedTask;
            }
        }
    }
}
