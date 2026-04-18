using PSTT.Data;
using System;

namespace PSTT.Data
{
    /// <summary>
    /// Configuration settings for DataSource instances.
    /// Used by CacheBuilder to construct configured instances.
    /// </summary>
    public class CacheConfig<TKey, TValue> where TKey : notnull
    {
        /// <summary>
        /// Maximum number of concurrent callbacks allowed per subscription.
        /// -1 = unlimited, 0 = serialize all callbacks, >0 = limit concurrent callbacks
        /// Default: 2
        /// </summary>
        public int MaxCallbackConcurrency { get; set; } = 2;

        /// <summary>
        /// Maximum number of topics (unique keys) allowed in the cache.
        /// 0 = unlimited
        /// Default: 1000
        /// </summary>
        public int MaxTopics { get; set; } = 1000;

        /// <summary>
        /// Maximum number of subscriptions allowed per topic.
        /// 0 = unlimited
        /// Default: 100
        /// </summary>
        public int MaxSubscriptionsPerTopic { get; set; } = 100;

        /// <summary>
        /// Maximum total number of subscriptions across all topics.
        /// 0 = unlimited
        /// Default: 10000
        /// </summary>
        public int MaxSubscriptionsTotal { get; set; } = 10000;

        /// <summary>
        /// Dispatcher for executing subscriber callbacks.
        /// Default: ThreadPoolDispatcher(waitForCompletion: false)
        /// </summary>
        public ICallbackDispatcher Dispatcher { get; set; } = new ThreadPoolDispatcher(waitForCompletion: false);

        /// <summary>
        /// Action to invoke when a callback throws an exception.
        /// Default: null (exceptions are counted but swallowed)
        /// </summary>
        public Action<Exception, string>? OnCallbackError { get; set; }

        /// <summary>
        /// Action to invoke for debug failures.
        /// Default: Debug.Fail
        /// </summary>
        public Action<string>? DebugFailHandler { get; set; }

        /// <summary>
        /// Action to invoke for debug assertions.
        /// Default: Debug.Assert
        /// </summary>
        public Action<bool, string>? DebugAssertHandler { get; set; }

        /// <summary>
        /// Enable hierarchical tree-based wildcard matching for topics.
        /// When enabled, supports MQTT-style wildcards: '+' (single level) and '#' (multi-level).
        /// Default: false
        /// </summary>
        public bool EnableWildcards { get; set; } = false;
    }
}
