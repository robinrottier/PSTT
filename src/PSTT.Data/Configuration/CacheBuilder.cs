using PSTT.Data;
using System;

namespace PSTT.Data
{

    /// <summary>
    /// Fluent builder for creating configured DataSource instances.
    /// Supports composition of features like wildcard support and upstream connections.
    /// </summary>
    /// <example>
    /// var dataSource = new CacheBuilder&lt;string, string&gt;()
    ///     .WithMaxTopics(5000)
    ///     .WithDispatcher(new SynchronousDispatcher())
    ///     .Build();
    /// </example>
    public class CacheBuilder<TKey, TValue> where TKey : notnull
    {
        private readonly CacheConfig<TKey, TValue> _config = new();
        private ICache<TKey, TValue>? _upstream;
        private bool _upstreamSupportsWildcards;
        private bool _forwardPublishToUpstream;
        private IWildcardMatcher<TKey>? _wildcardMatcher;

        /// <summary>
        /// Sets the maximum number of topics allowed.
        /// </summary>
        public CacheBuilder<TKey, TValue> WithMaxTopics(int maxTopics)
        {
            if (maxTopics < 0)
                throw new ArgumentOutOfRangeException(nameof(maxTopics), "Must be >= 0");
            _config.MaxTopics = maxTopics;
            return this;
        }

        /// <summary>
        /// Sets the maximum number of subscriptions per topic.
        /// </summary>
        public CacheBuilder<TKey, TValue> WithMaxSubscriptionsPerTopic(int max)
        {
            if (max < 0)
                throw new ArgumentOutOfRangeException(nameof(max), "Must be >= 0");
            _config.MaxSubscriptionsPerTopic = max;
            return this;
        }

        /// <summary>
        /// Sets the maximum total subscriptions across all topics.
        /// </summary>
        public CacheBuilder<TKey, TValue> WithMaxSubscriptionsTotal(int max)
        {
            if (max < 0)
                throw new ArgumentOutOfRangeException(nameof(max), "Must be >= 0");
            _config.MaxSubscriptionsTotal = max;
            return this;
        }

        /// <summary>
        /// Sets the maximum callback concurrency per subscription.
        /// </summary>
        public CacheBuilder<TKey, TValue> WithMaxCallbackConcurrency(int max)
        {
            if (max < -1)
                throw new ArgumentOutOfRangeException(nameof(max), "Must be >= -1");
            _config.MaxCallbackConcurrency = max;
            return this;
        }

        /// <summary>
        /// Sets the callback dispatcher strategy.
        /// </summary>
        public CacheBuilder<TKey, TValue> WithDispatcher(ICallbackDispatcher dispatcher)
        {
            _config.Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            return this;
        }

        /// <summary>
        /// Configures callbacks to execute synchronously on the calling thread.
        /// </summary>
        public CacheBuilder<TKey, TValue> WithSynchronousCallbacks()
        {
            _config.Dispatcher = new SynchronousDispatcher();
            return this;
        }

        /// <summary>
        /// Configures callbacks to execute on the thread pool.
        /// </summary>
        /// <param name="waitForCompletion">If true, waits for callbacks to complete</param>
        public CacheBuilder<TKey, TValue> WithThreadPoolCallbacks(bool waitForCompletion = false)
        {
            _config.Dispatcher = new ThreadPoolDispatcher(waitForCompletion);
            return this;
        }

        /// <summary>
        /// Sets the callback error handler.
        /// </summary>
        public CacheBuilder<TKey, TValue> WithCallbackErrorHandler(Action<Exception, string> handler)
        {
            _config.OnCallbackError = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        /// <summary>
        /// Enables hierarchical wildcard matching for topics.
        /// Supports MQTT-style wildcards: '+' (single level) and '#' (multi-level).
        /// Example: "sensors/+/temp" or "sensors/#"
        /// </summary>
        public CacheBuilder<TKey, TValue> WithWildcards()
        {
            _config.EnableWildcards = true;
            return this;
        }

        /// <summary>
        /// Sets the grace period before the upstream subscription is removed after the last local
        /// subscriber disposes. A new subscriber arriving within this window reuses the existing
        /// upstream connection rather than issuing a fresh unsubscribe/resubscribe.
        /// Use this to avoid MQTT traffic spikes during rapid page navigation.
        /// </summary>
        public CacheBuilder<TKey, TValue> WithUnsubscribeGracePeriod(TimeSpan period)
        {
            if (period < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(period), "Must be >= 0");
            _config.UnsubscribeGracePeriod = period;
            return this;
        }

        /// <summary>
        /// or the default <see cref="MqttWildcardMatcher"/> when called with no argument.
        /// Implicitly enables wildcard support (equivalent to also calling <see cref="WithWildcards"/>).
        /// </summary>
        /// <param name="matcher">
        /// A custom <see cref="IWildcardMatcher{TKey}"/> implementation. Pass <c>null</c> (or omit)
        /// to use the built-in MQTT matcher.
        /// </param>
        public CacheBuilder<TKey, TValue> WithPatternMatching(IWildcardMatcher<TKey>? matcher = null)
        {
            _config.EnableWildcards = true;
            _wildcardMatcher = matcher;
            return this;
        }

        /// <summary>
        /// Configures an upstream DataSource for cascading lookups.
        /// When a subscription is created, it will automatically forward to the upstream source.
        /// Can be combined with WithWildcards() to support both features together.
        /// </summary>
        /// <param name="upstream">The upstream DataSource to forward subscriptions to.</param>
        /// <param name="supportsWildcards">
        /// When true, wildcard subscriptions ('+' and '#') are forwarded to the upstream source.
        /// The upstream must support wildcard matching (e.g., built with WithWildcards()).
        /// When false (default), wildcards are satisfied locally only.
        /// </param>
        /// <param name="forwardPublish">
        /// When true, PublishAsync calls on this DataSource are also forwarded to the upstream.
        /// Use this for write-through scenarios where a local publish should also reach the upstream
        /// backend (e.g. an MqttCache that forwards values to the broker).
        /// Default is false.
        /// </param>
        public CacheBuilder<TKey, TValue> WithUpstream(ICache<TKey, TValue> upstream, bool supportsWildcards = false, bool forwardPublish = false)
        {
            _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
            _upstreamSupportsWildcards = supportsWildcards;
            _forwardPublishToUpstream = forwardPublish;
            return this;
        }

        /// <summary>
        /// Builds the configured DataSource instance.
        /// Returns CacheWithPatterns when WithWildcards() was called, otherwise base DataSource.
        /// Upstream (if set via WithUpstream()) is wired in after construction.
        /// </summary>
        public Cache<TKey, TValue> Build()
        {
            Cache<TKey, TValue> ds;

            if (_config.EnableWildcards)
                ds = new CacheWithWildcards<TKey, TValue>(_config, _wildcardMatcher);
            else
                ds = new Cache<TKey, TValue>(_config);

            if (_upstream != null)
                ds.SetUpstream(_upstream, _upstreamSupportsWildcards, _forwardPublishToUpstream);

            return ds;
        }
    }
}
