namespace PSTT.Data
{
    /// <summary>
    /// A cache that bridges specific subscription patterns from a <em>source</em> cache into an
    /// isolated <em>local</em> cache. Subscribers on <see cref="BridgeCache{TKey,TValue}"/> only
    /// see data that has arrived via the configured bridge patterns; their subscriptions are
    /// satisfied locally and do NOT propagate upstream through the source chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call <see cref="SetBridges"/> to configure which patterns are forwarded from <see cref="Source"/>
    /// into the local view. Each pattern results in a subscription on <paramref name="source"/>; when
    /// matching data arrives it is published (retained) into <see cref="Local"/>.
    /// </para>
    /// <para>
    /// <b>Subscribe / read operations</b> (Subscribe, SubscribeAsync, GetValue, TryGetValue,
    /// GetSnapshot, Unsubscribe, Count, Clear) are all delegated to the internal
    /// <see cref="Local"/> cache.  A subscription for a key that is not covered by any bridge
    /// pattern will stay in <see cref="IStatus.StateValue.Pending"/> forever.
    /// </para>
    /// <para>
    /// <b>Publish operations</b> (PublishAsync, RegisterPublisher) are delegated to
    /// <see cref="Source"/>, so publishes reach the broker / upstream chain.
    /// To publish within this dashboard session only (not reaching upstream), use
    /// <see cref="Local"/> directly.
    /// </para>
    /// <para>
    /// Empty bridge pattern list → no data delivered → all subscriptions stay Pending.
    /// </para>
    /// </remarks>
    public sealed class BridgeCache<TKey, TValue> : ICache<TKey, TValue>
        where TKey : notnull
    {
        private readonly ICache<TKey, TValue> _source;
        private readonly CacheWithWildcards<TKey, TValue> _local;
        private readonly List<ISubscription<TKey, TValue>> _bridges = [];

        /// <summary>The upstream source cache (publishes reach this and propagate to broker).</summary>
        public ICache<TKey, TValue> Source => _source;

        /// <summary>
        /// The isolated local view. Only data that has arrived via bridge patterns is present here.
        /// Publishing to this cache stays within the current session and never reaches upstream.
        /// </summary>
        public ICache<TKey, TValue> Local => _local;

        public BridgeCache(ICache<TKey, TValue> source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _local = new CacheWithWildcards<TKey, TValue>();
        }

        /// <summary>
        /// Configures the bridge patterns. Disposes any existing bridges, clears the local cache,
        /// then subscribes to each <paramref name="patterns"/> on <see cref="Source"/>. Matching
        /// data is forwarded (retained) into <see cref="Local"/>.
        /// </summary>
        public void SetBridges(IEnumerable<TKey> patterns)
        {
            foreach (var sub in _bridges) sub.Dispose();
            _bridges.Clear();
            _local.Clear();

            foreach (var pattern in patterns)
            {
                var sub = _source.Subscribe(pattern, async s =>
                {
                    if (s.Status.IsPending) return;
                    await _local.PublishAsync(s.Key, s.Value, s.Status, retain: true);
                });
                _bridges.Add(sub);
            }
        }

        // ── Subscribe / read → _local ──────────────────────────────────────────────

        public ISubscription<TKey, TValue> Subscribe(TKey key, Func<ISubscription<TKey, TValue>, Task> callback)
            => _local.Subscribe(key, callback);

        public Task<ISubscription<TKey, TValue>> SubscribeAsync(TKey key, Func<ISubscription<TKey, TValue>, Task> callback, CancellationToken cancellationToken = default)
            => _local.SubscribeAsync(key, callback, cancellationToken);

        public void Unsubscribe(ISubscription<TKey, TValue> subscription)
            => _local.Unsubscribe(subscription);

        public TValue? GetValue(TKey key)
            => _local.GetValue(key);

        public bool TryGetValue(TKey key, out TValue? value)
            => _local.TryGetValue(key, out value);

        public IReadOnlyDictionary<TKey, TValue> GetSnapshot()
            => _local.GetSnapshot();

        public int Count => _local.Count;

        public void Clear()
        {
            foreach (var sub in _bridges) sub.Dispose();
            _bridges.Clear();
            _local.Clear();
        }

        // ── Publish / register → _source (global) ─────────────────────────────────

        public Task PublishAsync(TKey key, TValue value, IStatus? status, bool retain = false, CancellationToken cancellationToken = default)
            => _source.PublishAsync(key, value, status, retain, cancellationToken);

        public Task PublishAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
            => _source.PublishAsync(key, value, cancellationToken);

        public Task PublishAsync(TKey key, IStatus status, CancellationToken cancellationToken = default)
            => _source.PublishAsync(key, status, cancellationToken);

        public IPublisher<TKey, TValue> RegisterPublisher(TKey key, IStatus? disposeStatus = null)
            => _source.RegisterPublisher(key, disposeStatus);
    }
}
