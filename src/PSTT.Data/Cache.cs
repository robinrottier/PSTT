using PSTT.Data;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("PSTT.Data.Tests")]
[assembly: InternalsVisibleTo("PSTT.Mqtt")]
[assembly: InternalsVisibleTo("PSTT.Remote")]

namespace PSTT.Data
{
    public class Status : IStatus
    {
        public Status() { State = IStatus.StateValue.Pending; Code = 0; Message = null; }
        public IStatus.StateValue State { get; set; }
        public string? Message { get; set; }
        public int Code { get; set; }

    }

    // a single subscription to a topic
    // ...belongs to a wrapper which holds actual key, values and a list of these which get fired to each subscriber
    class Subscription<TKey, TValue> : ISubscription<TKey, TValue>
                where TKey : notnull
    {
        public TKey Key { get { return Collection.Key; } }
        public IStatus Status { get { return Collection.Status; } }
        public TValue Value { get { return Collection.Value; } }
        Func<ISubscription<TKey, TValue>, Task> Callback { get; }
        internal Subscription(CacheItem<TKey, TValue> col, Func<ISubscription<TKey, TValue>, Task> callback)
        {
            Collection = col ?? throw new ArgumentNullException(nameof(col));
            Callback = callback;
        }
        internal CacheItem<TKey, TValue> Collection { get; private set; }
        internal bool IsActive { get; set; } = true;
        public void Dispose()
        {
            IsActive = false;
        }

        public void HandleFailStatus()
        {
            // we cant clear collection reference as we stil need that to get the failed status ad message
            // - so this subcription just floats unattached from the collection (but still refering to it here)
            //   and will be cleaned up by GC when all references to it are gone
            // Collection = null!;
            IsActive = false; // Mark as inactive instead of using negative index
        }

        public async Task InvokeCallback(ISubscription<TKey, TValue>? subscription = null, CancellationToken cancellationToken = default)
        {
            // if we're still in a callback for this subscription then dont fire it again
            // we will lose this update but so what ...the subscriber just checks latest value anyway and we just updated it
            // what if its a change of status e.g. to fail? 

            if (subscription == null)
                subscription = this;

            try
            {
                Interlocked.Increment(ref InCallback);

                // ActiveCallbacks tracks actual callback body executions, including fire-and-forget
                // tasks dispatched to the thread pool that haven't completed yet.
                // InCallback drops back to 0 quickly for fire-and-forget dispatchers (as soon as
                // DispatchAsync returns), so it can't gate concurrent body executions accurately.
                // ActiveCallbacks stays elevated until the body finishes, making it the correct
                // counter for MaxCallbackConcurrency enforcement.
                var active = Interlocked.Increment(ref ActiveCallbacks);
                var max = Collection.MaxCallbackConcurrency;
                if (active > 0 && max > 0 && active > max)
                {
                    Interlocked.Decrement(ref ActiveCallbacks);
                    Interlocked.Increment(ref SkippedCallback);
                    return;
                }

                var dispatcher = Collection.Source.Config.Dispatcher;

                if (dispatcher.WaitsForCompletion)
                {
                    // Waiting path: hold ActiveCallbacks for the full duration of the callback.
                    try
                    {
                        await dispatcher.DispatchAsync(() => Callback(subscription), cancellationToken);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref ActiveCallbacks);
                    }
                }
                else
                {
                    // Fire-and-forget path: wrap the callback so ActiveCallbacks is decremented
                    // when the body actually finishes on the thread pool, not when InvokeCallback
                    // returns. Exception handling must also live inside the wrapper since the outer
                    // catch won't see exceptions from a discarded task.
                    var capturedSubscription = subscription;
                    var dispatchTask = dispatcher.DispatchAsync(async () =>
                    {
                        try
                        {
                            await Callback(capturedSubscription);
                        }
                        catch (Exception callbackEx)
                        {
                            Interlocked.Increment(ref ExceptionInCallback);
                            var errorHandler = Collection.Source.Config.OnCallbackError;
                            if (errorHandler != null)
                            {
                                try { errorHandler(callbackEx, $"Subscription callback for key '{Collection.Key}' threw exception"); }
                                catch { }
                            }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref ActiveCallbacks);
                        }
                    }, cancellationToken);

                    // If dispatch failed synchronously (e.g. pre-cancelled token), the wrapper
                    // body never ran and won't decrement ActiveCallbacks, so we must do it here.
                    if (dispatchTask.IsFaulted || dispatchTask.IsCanceled)
                        Interlocked.Decrement(ref ActiveCallbacks);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation is expected, just rethrow
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref ExceptionInCallback);

                // Invoke error handler if configured
                var errorHandler = Collection.Source.Config.OnCallbackError;
                if (errorHandler != null)
                {
                    try
                    {
                        errorHandler(ex, $"Subscription callback for key '{Collection.Key}' threw exception");
                    }
                    catch
                    {
                        // Ignore errors in error handler
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref InCallback);
            }
        }

        internal int InCallback = 0;
        internal int ActiveCallbacks = 0;
        internal int SkippedCallback = 0;
        internal int ExceptionInCallback = 0;
    }

    // collection of subscriptions for a given key
    // -- holds the actual key, value and status for the subscription
    // -- and fires updates to all the subscription callbacks when value/status changes
    internal class CacheItem<TKey, TValue> : ISubscription<TKey, TValue>
                where TKey : notnull
    {
        public TKey Key { get; }
        public IStatus Status { get; private set; }
        public TValue Value { get; private set; }
        public bool Retain { get; internal set; }
        // Guards against double-removal: Unsubscribe and HandleFailStatus may both see Count==0 concurrently.
        // Only the first caller that exchanges 0→1 proceeds with RemoveItem.
        internal int _removeOnce = 0;
        internal ConcurrentBag<Subscription<TKey, TValue>> Subscriptions { get; } = new();
        public CacheItem(Cache<TKey, TValue> source, TKey key, bool retain = false)
        {
            Source = source;
            Key = key;
            Status = new Status();
            Value = default!;
            Retain = retain;
        }

        internal Cache<TKey, TValue> Source { get; init; }
        public int Count => Subscriptions.Count(s => s.IsActive);

        /// <summary>
        /// Gets the maximum number of concurrent callbacks allowed for this instance.
        /// - virtual function allows derived class to adust, per instance if required
        /// - base class is DataSource setting
        /// </summary>
        internal virtual int MaxCallbackConcurrency => Source.MaxCallbackConcurrency;

        internal Subscription<TKey, TValue> Add(Func<ISubscription<TKey, TValue>, Task> callback)
        {
            var sub = new Subscription<TKey, TValue>(this, callback);
            Subscriptions.Add(sub);
            return sub;
        }

        // Upstream subscription — set by DataSource.NewItem when Source.Upstream != null
        internal ISubscription<TKey, TValue>? UpstreamSub { get; set; }

        internal async Task UpstreamCallback(ISubscription<TKey, TValue> sub)
        {
            await PublishAsync(sub.Value, sub.Status);
        }

        internal void Remove(Subscription<TKey, TValue> sub)
        {
            // Mark as inactive rather than removing from bag (ConcurrentBag doesn't support efficient removal)
            // The subscription will be cleaned up during periodic cleanup or when collection is disposed
            sub.IsActive = false;
            sub.Dispose();
        }
        public void Dispose()
        {
        }

        // TODO: consider if we should:
        // - fire callbacks in parallel or sequentially (currently parallel)
        // - wait for callbacks to complete before returning from PublishAsync (currently no)
        // - if waiting for callbacks to complete then should we return a Task which completes when all callbacks complete (currently no)
        // - if waiting for callbacks to complete then should we have a timeout for callbacks to complete (currently no)
        // - if waiting for callbacks to complete then should we have a way to cancel callbacks (currently no)
        // OR should any of the above be optional with parameters to the datasource ?
        // ALSO consider if Publish should return a dumy subcription, or maybe just this subscription collection?
        public async Task PublishAsync(TValue value, CancellationToken cancellationToken = default)
        {
            Value = value;
            // publish a value and it resets status
            if (!Status.IsActive)
            {
                Status.State = IStatus.StateValue.Active;
                Status.Message = null;
                Status.Code = 0;
            }
            await InvokeCallback(null, cancellationToken);
        }

        public async Task PublishAsync(IStatus status, CancellationToken cancellationToken = default)
        {
            // value remains unchanged
            // Clone the status to avoid sharing mutable state
            Status.State = status.State;
            Status.Message = status.Message;
            Status.Code = status.Code;
            await InvokeCallback(null, cancellationToken);
        }

        public async Task PublishAsync(TValue value, IStatus? status, CancellationToken cancellationToken = default)
        {
            Value = value;
            if (status == null)
            {
                if (!Status.IsActive)
                {
                    Status.State = IStatus.StateValue.Active;
                    Status.Message = null;
                    Status.Code = 0;
                }
            }
            else
                Status = status;
            await InvokeCallback(null, cancellationToken);
        }

        protected async Task InvokeCallback(ISubscription<TKey, TValue>? subscription = null, CancellationToken cancellationToken = default)
        {
            await Parallel.ForEachAsync(Subscriptions.Where(s => s.IsActive), cancellationToken, async (sub, ct) =>
            {
                // fire and dont wait for the Task to complete
                if (Source.WaitOnSubscriptionCallback)
                    await sub.InvokeCallback(subscription, ct);
                else
                    _ = sub.InvokeCallback(subscription, ct);
            });

            await OnInvokeCallback(cancellationToken);
        }

        protected virtual async Task OnInvokeCallback(CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
        }

        // Wildcard items override this to always return true because InitialInvokeAsync must walk
        // the subtree for existing matches regardless of the item's own status.
        internal virtual bool NeedsInitialInvoke => !Status.IsPending;

        internal virtual async Task InitialInvokeAsync(Subscription<TKey, TValue> sub, CancellationToken cancellationToken = default)
        {
            // if the sub already has a value then fire callback too
            if (!Status.IsPending)
            {
                //fire and dont wait for the Task to complete...or should it??
                if (Source.WaitOnSubscriptionCallback)
                    await sub.InvokeCallback(null, cancellationToken);
                else
                    _ = sub.InvokeCallback(null, cancellationToken);
            }
        }

        internal int HandleFailStatus()
        {
            if (!Status.IsFailed)
            {
                Cache<TKey, TValue>.DebugFail("HandleFailStatus() called but status is not failed");
                return 0;
            }
            int count = 0;
            foreach (var sub in Subscriptions.Where(s => s.IsActive))
            {
                sub.HandleFailStatus();
                count++;
            }
            return count;
        }

        // return number of subscribers still processing callback
        internal int InCallback { get { return SumForEachSubscription(s => s.InCallback); } }
        internal int ActiveCallbacks { get { return SumForEachSubscription(s => s.ActiveCallbacks); } }
        internal int SkippedCallback { get { return SumForEachSubscription(s => s.SkippedCallback); } }
        internal int ExceptionInCallback { get { return SumForEachSubscription(s => s.ExceptionInCallback); } }

        int SumForEachSubscription(Func<Subscription<TKey, TValue>, int> f)
        {
            int count = 0;
            foreach (var sub in Subscriptions.Where(s => s.IsActive))
            {
                count += f(sub);
            }
            return count;
        }

    }

    public class Cache<TKey, TValue> : ICache<TKey, TValue>
        where TKey : notnull
    {
        private readonly ConcurrentDictionary<TKey, CacheItem<TKey, TValue>> _cache = new();
        private int _topicCount = 0;

        /// <summary>
        /// Configuration for this DataSource instance.
        /// </summary>
        internal CacheConfig<TKey, TValue> Config { get; }

        /// <summary>
        /// Optional upstream DataSource for cascading lookups.
        /// Set via CacheBuilder.WithUpstream() — not part of CacheConfig.
        /// </summary>
        internal ICache<TKey, TValue>? Upstream { get; private set; }

        /// <summary>
        /// When true, wildcard subscriptions are forwarded to the upstream.
        /// The upstream must natively support wildcard matching.
        /// </summary>
        internal bool UpstreamSupportsWildcards { get; private set; }

        /// <summary>
        /// When true, PublishAsync calls are forwarded to the upstream after updating the local cache.
        /// Enables write-through behaviour: a publish on this DataSource also reaches the upstream
        /// (e.g. an MqttCache that sends the value to the broker).
        /// Note: subscribers may receive two notifications — one from the local update and one from
        /// the upstream echo — so callbacks should be idempotent.
        /// </summary>
        internal bool ForwardPublishToUpstream { get; private set; }

        /// <summary>
        /// Sets the upstream DataSource. Can be called after construction to wire in an upstream.
        /// </summary>
        public void SetUpstream(ICache<TKey, TValue> upstream, bool supportsWildcards = false, bool forwardPublish = false)
        {
            Upstream = upstream;
            UpstreamSupportsWildcards = supportsWildcards;
            ForwardPublishToUpstream = forwardPublish;
        }

        #region Config option for dictionary capacity and performance
        /// <summary>
        /// Limit to callback concurrency for individual subs.
        /// </summary>
        public int MaxCallbackConcurrency => Config.MaxCallbackConcurrency;

        public int MaxTopics => Config.MaxTopics;

        public int MaxSubscriptionsPerTopic => Config.MaxSubscriptionsPerTopic;

        public int MaxSubscriptionsTotal => Config.MaxSubscriptionsTotal;

        /// <summary>
        /// Whether publisher waits for all subscription callbacks to complete before returning from PublishAsync.
        /// </summary>
        public bool WaitOnSubscriptionCallback => Config.Dispatcher.WaitsForCompletion;
        #endregion

        #region Static dev / debug helpers
        // throw Fail exception (release or debug) for easy catch here
        public class FailException : ApplicationException
        {
            public FailException(string message) : base(message) { }
        }
        // use DebugFail and DebugAssert for debug only checks
        // - and test cases may override these for one-off tests where debug msg is expected but we dont want to fail the test
        public static Action<string> DebugFail = msg => Debug.Fail(msg);
        public static Action<bool, string> DebugAssert = (condition, msg) => Debug.Assert(condition, msg);
        #endregion

        /// <summary>
        /// Creates a DataSource with the specified configuration.
        /// For typical usage, use CacheBuilder instead.
        /// </summary>
        internal Cache(CacheConfig<TKey, TValue> config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));

            // Override static handlers if config provides them
            if (config.DebugFailHandler != null)
                DebugFail = config.DebugFailHandler;
            if (config.DebugAssertHandler != null)
                DebugAssert = config.DebugAssertHandler;
        }

        /// <summary>
        /// Creates a DataSource with default configuration.
        /// </summary>
        public Cache() : this(new CacheConfig<TKey, TValue>())
        {
        }

        public virtual void Clear()
        {
            _cache.Clear();
        }

        public int Count
        {
            get { return _cache.Count; }
        }

        public TValue? GetValue(TKey key)
        {
            TryGetValue(key, out var val);
            return val;
        }
        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
        {
            // already in collection
            if (_cache.TryGetValue(key, out var col))
            {
                value = col.Value;
                return true;
            }
            value = default;
            return false;
        }

        internal virtual CacheItem<TKey, TValue> NewItem(TKey key)
        {
            return new CacheItem<TKey, TValue>(this, key);
        }

        /// <summary>
        /// Ensures the cache item has an upstream subscription, attaching one lazily if needed.
        /// Called from <see cref="Subscribe"/> so that upstream subscriptions are only created
        /// when there is a real local subscriber — never as a side-effect of <see cref="PublishAsync"/>.
        /// Thread-safe: uses <c>lock(col)</c> to guard against concurrent Subscribe calls for the same key.
        /// </summary>
        internal virtual void AttachUpstream(CacheItem<TKey, TValue> col)
        {
            if (Upstream == null || col.UpstreamSub != null) return;
            lock (col)
            {
                if (col.UpstreamSub != null) return;
                col.UpstreamSub = Upstream.Subscribe(col.Key, col.UpstreamCallback);
            }
        }

        public ISubscription<TKey, TValue> Subscribe(TKey key, Func<ISubscription<TKey, TValue>, Task> callback)
        {
            // validate key
            if (key is null) throw new ArgumentNullException(nameof(key));

            // Increment subscription count first to reserve a slot
            var count = Interlocked.Increment(ref _subscribeCount);
            if (MaxSubscriptionsTotal > 0 && count > MaxSubscriptionsTotal)
            {
                Interlocked.Decrement(ref _subscribeCount);
                throw new InvalidOperationException("MaxSubscriptionsTotal limit exceeded");
            }

            try
            {
                // already in collection or new one
                var col = _cache.GetOrAdd(key, k =>
                {
                    // enforce MaxTopics when a new topic is being created
                    var newTopicCount = Interlocked.Increment(ref _topicCount);
                    if (MaxTopics > 0 && newTopicCount > MaxTopics)
                    {
                        Interlocked.Decrement(ref _topicCount);
                        throw new InvalidOperationException("MaxTopics limit exceeded");
                    }
                    return NewItem(k);
                });

                // Lazily attach upstream subscription when the first real subscriber arrives.
                // Items created by PublishAsync have no upstream sub yet; items created here
                // (new key) also need attaching. Both cases are handled identically.
                AttachUpstream(col);

                Subscription<TKey, TValue> sub;
                bool shouldInitialInvoke;
                lock (col)
                {
                    if (col.Count >= MaxSubscriptionsPerTopic && MaxSubscriptionsPerTopic > 0)
                    {
                        throw new InvalidOperationException("Subscription count per topic exceeded");
                    }

                    sub = col.Add(callback);
                    // Capture the status atomically with the subscription add. If there is already
                    // a value we must fire the initial callback; if not, any incoming value will
                    // arrive via InvokeCallback which already includes this subscriber.
                    shouldInitialInvoke = col.NeedsInitialInvoke;
                }

                if (shouldInitialInvoke)
                {
                    if (WaitOnSubscriptionCallback)
                        col.InitialInvokeAsync(sub).Wait();
                    else
                        _ = col.InitialInvokeAsync(sub);
                }

                return sub;
            }
            catch
            {
                // If we failed to add the subscription, decrement the count
                Interlocked.Decrement(ref _subscribeCount);
                throw;
            }
        }

        public void Unsubscribe(ISubscription<TKey, TValue> subscription)
        {
            Debug.Assert(subscription != null);
            Debug.Assert(subscription is Subscription<TKey, TValue>);

            // must already be in our collection
            if (_cache.TryGetValue(subscription.Key, out var col))
            {
                col.Remove((Subscription<TKey, TValue>)subscription);
                //subscription.Status = ICache.SubscriptionStatus.Failed; // mark as failed so it doesn't get any more updates
                Interlocked.Decrement(ref _subscribeCount);

                // was that the last request? if so, remove the collection
                // - unless its "retained" in which case we keep it around even with no subscribers
                // - use _removeOnce to prevent a concurrent HandleFailStatus from double-removing
                if (!col.Retain && col.Count == 0)
                {
                    if (Interlocked.Exchange(ref col._removeOnce, 1) == 0)
                        RemoveItem(col);
                }
                return;
            }

            DebugFail("Subscription not found in collection");
        }

        /// <inheritdoc/>
        public async Task<ISubscription<TKey, TValue>> SubscribeAsync(TKey key,
            Func<ISubscription<TKey, TValue>, Task> callback,
            CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            ISubscription<TKey, TValue>? sub = null;
            sub = Subscribe(key, async s =>
            {
                // Forward every callback to the caller's handler.
                await callback(s);

                // Resolve the task on the first non-Pending invocation.
                if (!s.Status.IsPending)
                    tcs.TrySetResult(true);
            });

            // If cancellation is requested, cancel the wait but keep the subscription alive.
            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                try
                {
                    await tcs.Task.ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // Rethrow as OperationCanceledException with the original token.
                    throw new System.OperationCanceledException(cancellationToken);
                }
            }

            return sub;
        }

        internal virtual void RemoveItem(CacheItem<TKey, TValue> col)
        {
            if (Upstream != null && col.UpstreamSub != null)
                Upstream.Unsubscribe(col.UpstreamSub);

            bool ret = _cache.Remove(col.Key, out var v);
            if (ret)
            {
                Interlocked.Decrement(ref _topicCount);
            }
            DebugAssert(ret, "Failed to remove subscription from cache");
            DebugAssert(ReferenceEquals(v, col), "Removed subscription does not match expected");
        }

        public virtual async Task PublishAsync(TKey key, TValue value, IStatus? status, bool retain = false, CancellationToken cancellationToken = default)
        {
            var subs = _cache.GetOrAdd(key, k => NewItem(k));
            subs.Retain = retain;
            await subs.PublishAsync(value, status, cancellationToken);
            HandleFailStatus(subs);
            if (ForwardPublishToUpstream && Upstream != null)
                await Upstream.PublishAsync(key, value, status, retain, cancellationToken);
        }
        public virtual async Task PublishAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
        {
            var subs = _cache.GetOrAdd(key, k => NewItem(k));
            await subs.PublishAsync(value, cancellationToken);
            if (ForwardPublishToUpstream && Upstream != null)
                await Upstream.PublishAsync(key, value, cancellationToken);
        }
        public virtual async Task PublishAsync(TKey key, IStatus status, CancellationToken cancellationToken = default)
        {
            var subs = _cache.GetOrAdd(key, k => NewItem(k));
            await subs.PublishAsync(status, cancellationToken);
            HandleFailStatus(subs);
        }

        void HandleFailStatus(CacheItem<TKey, TValue> subs)
        {
            // the sub is failed...pass to all subscribers and remove from our cache
            if (subs.Status.IsFailed)
            {
                Interlocked.Add(ref _subscribeCount, -subs.HandleFailStatus());
                if (Interlocked.Exchange(ref subs._removeOnce, 1) == 0)
                    RemoveItem(subs);
            }
            // or if subs has no subscribers and retain flag not set then remove from cache
            // Use _removeOnce to guard against racing with Unsubscribe which may have already
            // marked the last subscriber inactive and is about to call RemoveItem itself.
            else if (subs.Count == 0 && subs.Retain == false)
            {
                if (Interlocked.Exchange(ref subs._removeOnce, 1) == 0)
                    RemoveItem(subs);
            }
        }

        // "Doevents-like" to let any pending callbacks run
        // - simply does a context switch rather than active msg loop
        // - could we have something here to actively check status of all pending ops and await them?
        // - each pending callback would have to stick something in a collection somehow and remove it when it completes
        internal async Task DoEventsAsync()
        {
            //int before = InCallback;
            await Task.Delay(1);
            await Task.Yield();
            //int after = InCallback;
            //Debug.WriteLine($"DoEventsAsync(): {before - after} events processed ({before},{after})");
        }

        // return number of subscribers still processing callback
        internal int InCallback { get { return SumForEachCacheValue(s => s.InCallback); } }
        internal int ActiveCallbacks { get { return SumForEachCacheValue(s => s.ActiveCallbacks); } }
        internal int SkippedCallback { get { return SumForEachCacheValue(s => s.SkippedCallback); } }
        internal int ExceptionInCallback { get { return SumForEachCacheValue(s => s.ExceptionInCallback); } }
        public int SubscribeCount { get { return _subscribeCount; } }
        private int _subscribeCount = 0;

        internal int SubscriptionCount { get { return SumForEachCacheValue(s => s.Count); } }

        int SumForEachCacheValue(Func<CacheItem<TKey, TValue>, int> f)
        {
            int count = 0;
            foreach (var col in _cache.Values)
            {
                count += f(col);
            }
            return count;
        }

        #region IPublisher support

        private readonly ConcurrentDictionary<TKey, TopicPublisherEntry> _publisherEntries = new();

        /// <inheritdoc/>
        public IPublisher<TKey, TValue> RegisterPublisher(TKey key, IStatus? disposeStatus = null)
        {
            if (key is null) throw new ArgumentNullException(nameof(key));

            // Default dispose status is Stale — signals value is still cached but producer has gone away.
            var status = disposeStatus ?? new Status { State = IStatus.StateValue.Stale };

            var entry = _publisherEntries.GetOrAdd(key, _ => new TopicPublisherEntry());
            entry.AddRef();
            return new Publisher<TKey, TValue>(this, key, status, entry, _publisherEntries);
        }

        #endregion
    }

    /// <summary>Tracks the number of active publishers for a single topic.</summary>
    internal sealed class TopicPublisherEntry
    {
        private int _refCount;
        internal void AddRef() => Interlocked.Increment(ref _refCount);
        /// <summary>Returns the ref count AFTER decrement.</summary>
        internal int Release() => Interlocked.Decrement(ref _refCount);
    }

    /// <summary>Concrete <see cref="IPublisher{TKey,TValue}"/> returned by <see cref="DataSource{TKey,TValue}.RegisterPublisher"/>.</summary>
    internal sealed class Publisher<TKey, TValue> : IPublisher<TKey, TValue>
        where TKey : notnull
    {
        private readonly Cache<TKey, TValue> _source;
        private readonly IStatus _disposeStatus;
        private readonly TopicPublisherEntry _entry;
        private readonly ConcurrentDictionary<TKey, TopicPublisherEntry> _publisherEntries;
        private bool _disposed;

        internal Publisher(Cache<TKey, TValue> source, TKey key, IStatus disposeStatus,
            TopicPublisherEntry entry, ConcurrentDictionary<TKey, TopicPublisherEntry> publisherEntries)
        {
            _source = source;
            Key = key;
            _disposeStatus = disposeStatus;
            _entry = entry;
            _publisherEntries = publisherEntries;
        }

        public TKey Key { get; }

        public Task PublishAsync(TValue value, IStatus? status = null, bool retain = false, CancellationToken cancellationToken = default)
            => _source.PublishAsync(Key, value, status, retain, cancellationToken);

        public Task PublishAsync(TValue value, CancellationToken cancellationToken = default)
            => _source.PublishAsync(Key, value, cancellationToken);

        public Task PublishAsync(IStatus status, CancellationToken cancellationToken = default)
            => _source.PublishAsync(Key, status, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            if (_entry.Release() == 0)
            {
                // Last publisher for this topic — clean up the entry and publish the dispose status.
                _publisherEntries.TryRemove(Key, out _);
                await _source.PublishAsync(Key, _disposeStatus);
            }
        }
    }
}
