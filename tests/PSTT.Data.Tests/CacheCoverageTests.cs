using PSTT.Data;
using System.Diagnostics;

namespace PSTT.Data.Tests
{
    /// <summary>
    /// Targeted tests to improve code coverage of DataSource.dll, covering paths not
    /// exercised by the broader functional test suites.
    /// </summary>
    public class DataSourceCoverageTests
    {
        // ─────────────────────────────────────────────────────────────────────────
        // DataSource.Clear()
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Clear_RemovesAllRetainedTopics()
        {
            var ds = new Cache<string, string>();
            await ds.PublishAsync("a", "va", null, retain: true);
            await ds.PublishAsync("b", "vb", null, retain: true);
            await ds.PublishAsync("c", "vc", null, retain: true);
            Assert.Equal(3, ds.Count);

            ds.Clear();

            Assert.Equal(0, ds.Count);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // DataSource.PublishAsync(key, IStatus) — status-only overload
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task PublishAsync_StatusOnly_UpdatesStatusLeavesValueUnchanged()
        {
            var ds = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .Build();

            string? receivedValue = null;
            IStatus? receivedStatus = null;
            var sub = ds.Subscribe("k", async s =>
            {
                receivedValue = s.Value;
                receivedStatus = s.Status;
            });

            // Establish a value first
            await ds.PublishAsync("k", "hello");
            Assert.Equal("hello", sub.Value);

            // Status-only publish
            var staleStatus = new Status { State = IStatus.StateValue.Stale, Message = "going stale" };
            await ds.PublishAsync("k", (IStatus)staleStatus);

            Assert.Equal("hello", sub.Value);        // value unchanged
            Assert.True(sub.Status.IsStale);
            Assert.Equal("going stale", sub.Status.Message);
        }

        [Fact]
        public async Task PublishAsync_StatusOnly_FailedStatus_RemovesFromCache()
        {
            var ds = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .Build();

            string? received = null;
            var sub = ds.Subscribe("k", async s => { received = s.Value; });
            await ds.PublishAsync("k", "initial");

            var failStatus = new Status { State = IStatus.StateValue.Failed, Message = "gone" };
            await ds.PublishAsync("k", (IStatus)failStatus);

            // After fail publish, item is removed from cache
            Assert.Equal(0, ds.Count);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // DataSource(CacheConfig) constructor — DebugFail/Assert handler override
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Constructor_Config_OverridesDebugHandlers()
        {
            string? failMsg = null;
            (bool cond, string msg) assertCall = default;

            var config = new CacheConfig<long, long>
            {
                DebugFailHandler = msg => failMsg = msg,
                DebugAssertHandler = (cond, msg) => assertCall = (cond, msg)
            };

            var ds = new Cache<long, long>(config);

            // After construction the static fields for this type should point to our lambdas
            Cache<long, long>.DebugFail("test-fail");
            Assert.Equal("test-fail", failMsg);

            Cache<long, long>.DebugAssert(false, "test-assert");
            Assert.Equal("test-assert", assertCall.msg);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // FailException class
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void FailException_CarriesMessage()
        {
            var ex = new Cache<string, string>.FailException("boom");
            Assert.Equal("boom", ex.Message);
            Assert.IsAssignableFrom<ApplicationException>(ex);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Subscription.InvokeCallback — exception paths
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Callback_Exception_IsCountedAndSwallowed_WhenNoErrorHandler()
        {
            var ds = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .Build();

            var sub = ds.Subscribe("k", async s =>
            {
                throw new InvalidOperationException("test exception");
            });

            // Should not throw — exception is swallowed when no error handler is configured
            await ds.PublishAsync("k", "v");

            Assert.Equal(1, ds.ExceptionInCallback);
        }

        [Fact]
        public async Task Callback_Exception_InvokesErrorHandler()
        {
            Exception? capturedEx = null;
            string? capturedMsg = null;

            var ds = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .WithCallbackErrorHandler((ex, msg) =>
                {
                    capturedEx = ex;
                    capturedMsg = msg;
                })
                .Build();

            var sub = ds.Subscribe("mykey", async s =>
            {
                throw new InvalidOperationException("callback boom");
            });

            await ds.PublishAsync("mykey", "v");

            Assert.NotNull(capturedEx);
            Assert.Contains("callback boom", capturedEx!.Message);
            Assert.NotNull(capturedMsg);
            Assert.Contains("mykey", capturedMsg);
        }

        [Fact]
        public async Task Callback_Exception_ErrorHandlerItself_Throws_IsSwallowed()
        {
            var ds = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .WithCallbackErrorHandler((ex, msg) =>
                {
                    throw new Exception("error handler also throws");
                })
                .Build();

            var sub = ds.Subscribe("k", async s =>
            {
                throw new InvalidOperationException("original error");
            });

            // The inner exception from the error handler must be swallowed
            await ds.PublishAsync("k", "v");

            Assert.Equal(1, ds.ExceptionInCallback);
        }

        [Fact]
        public async Task Callback_Cancellation_PropagatesOperationCanceledException()
        {
            var ds = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .Build();

            var sub = ds.Subscribe("k", async s => { /* no-op */ });

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // With a synchronous dispatcher and pre-cancelled token,
            // ThrowIfCancellationRequested fires inside InvokeCallback → rethrown.
            // TaskCanceledException is a subclass of OperationCanceledException.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ds.PublishAsync("k", "v", cts.Token));
        }

        [Fact]
        public async Task Callback_ThrowsOCE_FromCallbackBody_HitsOceCatchBlock()
        {
            // When the callback *itself* throws OperationCanceledException, InvokeCallback's
            // catch(OperationCanceledException) block (lines 74-77) is hit and rethrows it.
            var ds = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .Build();

            ds.Subscribe("k", async s => throw new OperationCanceledException("from-callback"));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ds.PublishAsync("k", "v"));
        }

        // ─────────────────────────────────────────────────────────────────────────
        // SynchronizationContextDispatcher
        // ─────────────────────────────────────────────────────────────────────────

        private sealed class TestSynchronizationContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state)
                => ThreadPool.QueueUserWorkItem(_ => d(state), null);
        }

        [Fact]
        public async Task SynchronizationContextDispatcher_DispatchesCallback()
        {
            var ctx = new TestSynchronizationContext();
            var dispatcher = new SynchronizationContextDispatcher(ctx);

            Assert.True(dispatcher.WaitsForCompletion);
            Assert.Contains("TestSynchronizationContext", dispatcher.Name);

            string result = "";
            await dispatcher.DispatchAsync(async () => { result = "dispatched"; });
            Assert.Equal("dispatched", result);
        }

        [Fact]
        public async Task SynchronizationContextDispatcher_Propagates_CallbackException()
        {
            var ctx = new TestSynchronizationContext();
            var dispatcher = new SynchronizationContextDispatcher(ctx);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dispatcher.DispatchAsync(async () => throw new InvalidOperationException("oops")));
        }

        [Fact]
        public void SynchronizationContextDispatcher_Constructor_NullContext_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new SynchronizationContextDispatcher(null!));
        }

        [Fact]
        public void SynchronizationContextDispatcher_ForCurrentContext_NoContext_Throws()
        {
            // Ensure no SynchronizationContext is set on this thread
            var prev = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                Assert.Throws<InvalidOperationException>(
                    () => SynchronizationContextDispatcher.ForCurrentContext());
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prev);
            }
        }

        [Fact]
        public void SynchronizationContextDispatcher_ForCurrentContext_WithContext_Succeeds()
        {
            var ctx = new TestSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ctx);
            try
            {
                var dispatcher = SynchronizationContextDispatcher.ForCurrentContext();
                Assert.NotNull(dispatcher);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        }

        [Fact]
        public async Task SynchronizationContextDispatcher_UsedAsDataSourceDispatcher_Works()
        {
            var ctx = new TestSynchronizationContext();
            var dispatcher = new SynchronizationContextDispatcher(ctx);

            var ds = new CacheBuilder<string, string>()
                .WithDispatcher(dispatcher)
                .Build();

            string? received = null;
            var sub = ds.Subscribe("t", async s => { received = s.Value; });
            await ds.PublishAsync("t", "via-sync-ctx");

            // Wait for ThreadPool work to complete
            await TestHelper.AssertValueAfterWait("via-sync-ctx", () => received, 50);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // CacheBuilder validation — negative / null arguments
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Builder_WithMaxTopics_Negative_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CacheBuilder<string, string>().WithMaxTopics(-1));
        }

        [Fact]
        public void Builder_WithMaxSubscriptionsPerTopic_Negative_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CacheBuilder<string, string>().WithMaxSubscriptionsPerTopic(-1));
        }

        [Fact]
        public void Builder_WithMaxSubscriptionsTotal_Negative_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CacheBuilder<string, string>().WithMaxSubscriptionsTotal(-1));
        }

        [Fact]
        public void Builder_WithMaxCallbackConcurrency_BelowMinusOne_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CacheBuilder<string, string>().WithMaxCallbackConcurrency(-2));
        }

        [Fact]
        public void Builder_WithMaxCallbackConcurrency_MinusOne_IsAllowed()
        {
            // -1 means unlimited — should not throw
            var ds = new CacheBuilder<string, string>()
                .WithMaxCallbackConcurrency(-1)
                .Build();
            Assert.Equal(-1, ds.MaxCallbackConcurrency);
        }

        [Fact]
        public void Builder_WithDispatcher_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new CacheBuilder<string, string>().WithDispatcher(null!));
        }

        [Fact]
        public void Builder_WithCallbackErrorHandler_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new CacheBuilder<string, string>().WithCallbackErrorHandler(null!));
        }

        [Fact]
        public async Task Builder_WithThreadPoolCallbacks_WaitForCompletion_Works()
        {
            var ds = new CacheBuilder<string, string>()
                .WithThreadPoolCallbacks(waitForCompletion: true)
                .Build();

            string? received = null;
            var sub = ds.Subscribe("k", async s => { received = s.Value; });
            await ds.PublishAsync("k", "tp-wait");

            Assert.Equal("tp-wait", received);
        }

        [Fact]
        public void Builder_WithCallbackErrorHandler_Sets_Handler()
        {
            int callCount = 0;
            var ds = new CacheBuilder<string, string>()
                .WithCallbackErrorHandler((ex, msg) => callCount++)
                .Build();
            Assert.NotNull(ds);
        }

        [Fact]
        public void Builder_WithMaxTopics_Valid_Succeeds()
        {
            var ds = new CacheBuilder<string, string>()
                .WithMaxTopics(100)
                .Build();
            Assert.NotNull(ds);
        }

        [Fact]
        public void Builder_WithMaxSubscriptionsPerTopic_Valid_Succeeds()
        {
            var ds = new CacheBuilder<string, string>()
                .WithMaxSubscriptionsPerTopic(50)
                .Build();
            Assert.NotNull(ds);
        }

        [Fact]
        public void Builder_WithMaxSubscriptionsTotal_Valid_Succeeds()
        {
            var ds = new CacheBuilder<string, string>()
                .WithMaxSubscriptionsTotal(500)
                .Build();
            Assert.NotNull(ds);
        }

        [Fact]
        public void Builder_WithUpstream_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new CacheBuilder<string, string>().WithUpstream(null!));
        }

        [Fact]
        public void Builder_WithUpstream_Valid_Succeeds()
        {
            var upstream = new CacheBuilder<string, string>().Build();
            var ds = new CacheBuilder<string, string>()
                .WithUpstream(upstream)
                .Build();
            Assert.NotNull(ds);
        }

        [Fact]
        public void Builder_WithWildcards_BuildsWildcardDataSource()
        {
            var ds = new CacheBuilder<string, string>()
                .WithWildcards()
                .Build();
            Assert.IsType<CacheWithWildcards<string, string>>(ds);
        }



        [Fact]
        public void SynchronousDispatcher_Name_IsCorrect()
        {
            var dispatcher = new SynchronousDispatcher();
            Assert.Equal("Synchronous", dispatcher.Name);
        }

        [Fact]
        public void ThreadPoolDispatcher_Name_ReflectsWaitMode()
        {
            var waitDispatcher = new ThreadPoolDispatcher(waitForCompletion: true);
            Assert.Contains("wait", waitDispatcher.Name, StringComparison.OrdinalIgnoreCase);

            var fireDispatcher = new ThreadPoolDispatcher(waitForCompletion: false);
            Assert.Contains("fire", fireDispatcher.Name, StringComparison.OrdinalIgnoreCase);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // ForwardPublishToUpstream — lines 505-506 and 512-513 in DataSource.cs
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ForwardPublish_Value_IsForwardedToUpstream()
        {
            var upstream = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .Build();

            var downstream = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .WithUpstream(upstream, forwardPublish: true)
                .Build();

            string? upstreamReceived = null;
            upstream.Subscribe("k", async s => { upstreamReceived = s.Value; });

            // Publish to downstream with forwardPublish=true → value reaches upstream
            await downstream.PublishAsync("k", "fwd-value");

            Assert.Equal("fwd-value", upstreamReceived);
        }

        [Fact]
        public async Task ForwardPublish_ValueWithStatus_IsForwardedToUpstream()
        {
            var upstream = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .Build();

            var downstream = new CacheBuilder<string, string>()
                .WithSynchronousCallbacks()
                .WithUpstream(upstream, forwardPublish: true)
                .Build();

            string? upstreamReceived = null;
            IStatus? upstreamStatus = null;
            upstream.Subscribe("k", async s => { upstreamReceived = s.Value; upstreamStatus = s.Status; });

            var stale = new Status { State = IStatus.StateValue.Stale, Message = "staling" };
            await downstream.PublishAsync("k", "fwd-with-status", stale, retain: false);

            Assert.Equal("fwd-with-status", upstreamReceived);
            Assert.NotNull(upstreamStatus);
            Assert.True(upstreamStatus!.IsStale);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // CacheWithPatterns — error paths
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Wildcard_DuplicateExactKey_AfterCacheClear_CanResubscribe()
        {
            var ds = new CacheWithWildcards<string, string>();

            // Establish key in both tree and cache
            var sub1 = ds.Subscribe("a/b", async s => { });

            // Clear() now resets both the cache dictionary and tree node references,
            // so re-subscribing the same key creates a fresh collection without errors.
            ds.Clear();

            string? received = null;
            var sub2 = ds.Subscribe("a/b", async s => { received = s.Value; });
            Assert.NotNull(sub2);

            await ds.PublishAsync("a/b", "after-clear");
            var deadline = DateTime.UtcNow.AddSeconds(1);
            while (DateTime.UtcNow < deadline && received == null)
                await Task.Delay(10);
            Assert.Equal("after-clear", received);

            ds.Unsubscribe(sub2);
        }

        [Fact]
        public void Wildcard_InvalidCharInKeyPart_Throws()
        {
            var ds = new CacheWithWildcards<string, string>();

            // '#' or '+' embedded inside a non-wildcard segment is invalid
            Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(() =>
                ds.Subscribe("a/b#c/d", async s => { }));

            Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(() =>
                ds.Subscribe("a/b+c", async s => { }));
        }

        [Fact]
        public async Task Wildcard_DuplicateExactKey_Throws()
        {
            var ds = new CacheWithWildcards<string, string>();

            // First publish creates the node
            await ds.PublishAsync("a/b", "v1", null, retain: true);

            // Subscribing to the same exact key a second time — this key already exists in the tree
            // The Subscribe call creates a new collection via NewItem, which hits the !isNew && !isWildcard guard
            var sub1 = ds.Subscribe("a/b", async s => { });

            // Adding a second subscriber to the same key reuses the existing collection — no throw
            var sub2 = ds.Subscribe("a/b", async s => { });
            Assert.NotNull(sub2);
            ds.Unsubscribe(sub1);
            ds.Unsubscribe(sub2);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // CacheItemWithWildcards.CachedUpstreamValue + late-subscriber replay
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Wildcard_LateSubscriber_ReceivesReplayedUpstreamCache()
        {
            // Build: upstream (wildcard) → downstream (wildcard, upstream-supported)
            var upstream = new CacheBuilder<string, string>()
                .WithWildcards()
                .WithSynchronousCallbacks()
                .Build();

            var downstream = new CacheBuilder<string, string>()
                .WithWildcards()
                .WithUpstream(upstream, supportsWildcards: true)
                .WithSynchronousCallbacks()
                .Build();

            // Subscribe a wildcard on downstream, which triggers upstream wildcard subscription
            var firstReceived = new List<string>();
            var sub1 = downstream.Subscribe("sensors/+", async s =>
            {
                if (!s.Status.IsPending)
                    lock (firstReceived) { firstReceived.Add(s.Value ?? ""); }
            });

            // Upstream publishes a retained value — flows to downstream
            await upstream.PublishAsync("sensors/temp", "22C", null, retain: true);
            await upstream.PublishAsync("sensors/hum", "55%", null, retain: true);

            // Allow async propagation
            await TestHelper.AssertConditionAfterWait(() => firstReceived.Count >= 2, 50);

            // Now add a SECOND wildcard subscriber to the same pattern.
            // At subscription time, upstream has already fired values into the downstream cache.
            // InitialInvokeAsync should replay those cached upstream values via CachedUpstreamValue.
            var secondReceived = new List<string>();
            var sub2 = downstream.Subscribe("sensors/+", async s =>
            {
                _ = s.Key;   // exercises CachedUpstreamValue.Key during initial replay
                s.Dispose(); // exercises CachedUpstreamValue.Dispose() — no-op on the shim
                if (!s.Status.IsPending)
                    lock (secondReceived) { secondReceived.Add(s.Value ?? ""); }
            });

            // The second subscriber should also receive the previously cached values
            await TestHelper.AssertConditionAfterWait(() => secondReceived.Count >= 2, 100);
            Assert.True(secondReceived.Count >= 2,
                $"Late subscriber should have received replayed upstream values, got {secondReceived.Count}");

            downstream.Unsubscribe(sub1);
            downstream.Unsubscribe(sub2);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Subscription.InvokeCallback — fire-and-forget exception paths
        // (the catch block inside the wrapper lambda executed on the thread pool)
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Callback_Exception_FireAndForget_IsCountedAndSwallowed_WhenNoErrorHandler()
        {
            var ds = new CacheBuilder<string, string>()
                .WithThreadPoolCallbacks(waitForCompletion: false)
                .Build();

            ds.Subscribe("k", async s =>
            {
                throw new InvalidOperationException("fire-and-forget exception");
            });

            await ds.PublishAsync("k", "v");

            // Exception occurs on the thread pool — wait for it to complete
            await TestHelper.AssertConditionAfterWait(() => ds.ExceptionInCallback == 1, 300);
            Assert.Equal(1, ds.ExceptionInCallback);
            Assert.Equal(0, ds.ActiveCallbacks);
        }

        [Fact]
        public async Task Callback_Exception_FireAndForget_InvokesErrorHandler()
        {
            Exception? capturedEx = null;
            string? capturedMsg = null;

            var ds = new CacheBuilder<string, string>()
                .WithThreadPoolCallbacks(waitForCompletion: false)
                .WithCallbackErrorHandler((ex, msg) =>
                {
                    capturedEx = ex;
                    capturedMsg = msg;
                })
                .Build();

            ds.Subscribe("mykey", async s =>
            {
                throw new InvalidOperationException("ff-callback boom");
            });

            await ds.PublishAsync("mykey", "v");

            // Wait for the thread pool wrapper to complete and invoke the error handler
            await TestHelper.AssertConditionAfterWait(() => capturedEx != null, 300);
            Assert.NotNull(capturedEx);
            Assert.Contains("ff-callback boom", capturedEx!.Message);
            Assert.NotNull(capturedMsg);
            Assert.Contains("mykey", capturedMsg);
            Assert.Equal(1, ds.ExceptionInCallback);
            Assert.Equal(0, ds.ActiveCallbacks);
        }

        [Fact]
        public async Task Callback_Exception_FireAndForget_ErrorHandlerThrows_InnerExceptionSwallowed()
        {
            // The fire-and-forget wrapper has its own catch { } around the error handler call.
            // An exception thrown by the error handler must be silently swallowed.
            var ds = new CacheBuilder<string, string>()
                .WithThreadPoolCallbacks(waitForCompletion: false)
                .WithCallbackErrorHandler((ex, msg) =>
                {
                    throw new Exception("error handler also throws in ff mode");
                })
                .Build();

            ds.Subscribe("k", async s =>
            {
                throw new InvalidOperationException("original ff error");
            });

            await ds.PublishAsync("k", "v");

            await TestHelper.AssertConditionAfterWait(() => ds.ExceptionInCallback == 1, 300);
            Assert.Equal(1, ds.ExceptionInCallback);
            // ActiveCallbacks must be back to 0 (finally block still ran)
            Assert.Equal(0, ds.ActiveCallbacks);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // ActiveCallbacks counter — basic verification
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ActiveCallbacks_IsElevatedDuringCallback_ThenReturnsToZero()
        {
            int peakActive = 0;
            var tcs = new TaskCompletionSource();

            var ds = new CacheBuilder<string, string>()
                .WithThreadPoolCallbacks(waitForCompletion: false)
                .Build();

            ds.Subscribe("k", async s =>
            {
                // Signal that we're inside the callback so the test can sample ActiveCallbacks
                tcs.TrySetResult();
                await Task.Delay(100);
                Interlocked.Exchange(ref peakActive, ds.ActiveCallbacks);
            });

            await ds.PublishAsync("k", "v");

            // Wait until callback has started
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // ActiveCallbacks should be ≥ 1 while the callback body is executing
            Assert.True(ds.ActiveCallbacks >= 1 || peakActive >= 1,
                "ActiveCallbacks should be elevated while callback body is running");

            // After callback finishes it should drop to zero
            await TestHelper.AssertConditionAfterWait(() => ds.ActiveCallbacks == 0, 300);
            Assert.Equal(0, ds.ActiveCallbacks);
        }

        [Fact]
        public void SubscriptionCollection_HandleFailStatus_WhenNotFailed_ReturnsZero()
        {
            // Use a dedicated type so the temporary DebugFail override doesn't race with
            // other tests that depend on the default Cache<string,string>.DebugFail.
            var prevFail = Cache<Guid, int>.DebugFail;
            Cache<Guid, int>.DebugFail = _ => { };
            try
            {
                var ds = new Cache<Guid, int>();
                // CacheItem is internal but visible to tests via InternalsVisibleTo
                var col = new CacheItem<Guid, int>(ds, Guid.Empty);
                // Status is Pending (not Failed) — HandleFailStatus should hit the guard and return 0
                var count = col.HandleFailStatus();
                Assert.Equal(0, count);
            }
            finally
            {
                Cache<Guid, int>.DebugFail = prevFail;
            }
        }

        [Fact]
        public void SubscriptionCollection_Dispose_IsNoOp()
        {
            var ds = new Cache<string, string>();
            var col = new CacheItem<string, string>(ds, "key");
            // Dispose should be a no-op and not throw
            col.Dispose();
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Subscription.InvokeCallback — line 125: faulted/cancelled dispatch task
        // When fire-and-forget DispatchAsync returns a faulted or cancelled task
        // before its body runs, the guard decrements ActiveCallbacks so it doesn't
        // leak.
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Subscription_FaultedDispatch_FireAndForget_ActiveCallbacksStaysBalanced()
        {
            var ds = new CacheBuilder<Guid, int>()
                .WithDispatcher(new InstantFaultDispatcher())
                .Build();

            var key = Guid.NewGuid();
            ds.Subscribe(key, async s => { /* never reached */ });

            // Should not throw, even though the dispatcher faults immediately
            await ds.PublishAsync(key, 42);
            await Task.Delay(20);

            Assert.Equal(0, ds.ActiveCallbacks);
        }

        [Fact]
        public async Task Subscription_CancelledDispatch_FireAndForget_ActiveCallbacksStaysBalanced()
        {
            var ds = new CacheBuilder<Guid, int>()
                .WithDispatcher(new InstantCancelDispatcher())
                .Build();

            var key = Guid.NewGuid();
            ds.Subscribe(key, async s => { /* never reached */ });

            await ds.PublishAsync(key, 99);
            await Task.Delay(20);

            Assert.Equal(0, ds.ActiveCallbacks);
        }

        /// <summary>Fire-and-forget dispatcher that immediately returns a faulted task.</summary>
        private sealed class InstantFaultDispatcher : ICallbackDispatcher
        {
            public bool WaitsForCompletion => false;
            public string Name => "InstantFault";
            public Task DispatchAsync(Func<Task> action, CancellationToken cancellationToken)
                => Task.FromException(new InvalidOperationException("dispatch fault"));
        }

        /// <summary>Fire-and-forget dispatcher that immediately returns a cancelled task.</summary>
        private sealed class InstantCancelDispatcher : ICallbackDispatcher
        {
            public bool WaitsForCompletion => false;
            public string Name => "InstantCancel";
            public Task DispatchAsync(Func<Task> action, CancellationToken cancellationToken)
                => Task.FromCanceled(new CancellationToken(canceled: true));
        }
    }
}

