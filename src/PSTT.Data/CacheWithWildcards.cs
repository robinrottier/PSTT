using PSTT.Data;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text;
using System.IO.MemoryMappedFiles;

namespace PSTT.Data
{
    public class CacheWithWildcards<TKey, TValue> : Cache<TKey, TValue>
        where TKey : notnull
    {
        private readonly IWildcardMatcher<TKey>? _matcher;

        /// <summary>
        /// The pattern matcher used to determine whether a key is a wildcard and to match patterns
        /// against candidate keys. Defaults to <see cref="MqttWildcardMatcher"/> when <typeparamref name="TKey"/>
        /// is <see cref="string"/> and no matcher is supplied.
        /// </summary>
        public IWildcardMatcher<TKey>? WildcardMatcher => _matcher;

        public CacheWithWildcards() : this(new CacheConfig<TKey, TValue>(), null)
        {
        }

        public CacheWithWildcards(CacheConfig<TKey, TValue> config) : this(config, null)
        {
        }

        public CacheWithWildcards(CacheConfig<TKey, TValue> config, IWildcardMatcher<TKey>? matcher) : base(config)
        {
            _matcher = matcher ?? (typeof(TKey) == typeof(string) ? (IWildcardMatcher<TKey>)(object)new MqttWildcardMatcher() : null);
        }

        internal class TreeNode
        {
            public TreeNode(TKey key, ItemNode? parent)
            {
                KeyPart = key;
                Parent = parent;
                if (parent == null)//rootnode
                    throw new InvalidOperationException($"Parent node is null");
                else if (parent.Path == null)
                    throw new InvalidOperationException($"Parent node '{parent.KeyPart}' has null path");
                else if (parent.Path == "")
                    Path = key.ToString()!;
                else
                    Path = $"{parent.Path}/{key}";
            }

            // dummy constructor for testing only...
            internal TreeNode(TKey key, string path)
            {
                KeyPart = key;
                Parent = null;
                Path = path;
            }

            public TKey KeyPart { get; init; }
            public string Path {  get; init; }
            public ItemNode? Parent { get; init; }
            public CacheItemWithWildcards Sub { get; set; } = null!;
        }

        internal class ItemNode : TreeNode
        {
            public ItemNode(TKey key, ItemNode? parent) : base(key, parent)
            {
            }
            // dummy constructor for testing only...
            internal ItemNode(TKey key, string path) : base(key, path)
            {
            }

            public List<ItemNode> Children { get; init; } = new();

            // if the node a wildcard?
            // in which case the key below the wildcard is saved as a filter below this node
            // - any node below us that updates then searches up the tree looking for wildcard
            //   and if matches then updates that sub too
            public List<TreeNode> Filters { get; init; } = new();
        }

        internal class RootNode : ItemNode
        {
            public RootNode() : base(default!, "")
            {
            }

        }

        internal class FilterNode : TreeNode
        {
            private readonly IWildcardMatcher<TKey>? _matcher;

            /// <summary>
            /// The full subscription pattern string, e.g. "#", "sensors/#", "sensors/+/temp", "$DASHBOARD/#".
            /// Used to delegate matching to the configured <see cref="IWildcardMatcher{TKey}"/>.
            /// </summary>
            public string FullPattern { get; init; }

            public FilterNode(TKey key, ItemNode parent, string[] filter, IWildcardMatcher<TKey>? matcher = null) : base(key, parent)
            {
                if (filter == null) throw new ArgumentNullException(nameof(filter));
                if (filter.Length == 0) throw new ArgumentException("Filter array must have at least one part", nameof(filter));

                // Validate that the first filter part is a wildcard token using the configured matcher
                // when available, falling back to MQTT-specific check for null matcher / non-string TKey.
                bool firstPartIsWildcard = (matcher != null && filter[0] is TKey key0)
                    ? matcher.IsPattern(key0)
                    : (filter[0] == "#" || filter[0] == "+");
                if (!firstPartIsWildcard)
                    throw new ArgumentException("First part of filter array must be a wildcard (# or +)", nameof(filter));

                _matcher = matcher;
                Filter = filter;
                FullPattern = parent.Path.Length == 0
                    ? string.Join("/", filter)
                    : parent.Path + "/" + string.Join("/", filter);
            }

            // Test-only constructor — no matcher, uses fallback matching logic.
            internal FilterNode(TKey key, string path, string[] filter) : base(key, path)
            {
                Filter = filter;
                _matcher = null;
                FullPattern = (path?.Length ?? 0) == 0
                    ? string.Join("/", filter)
                    : path + "/" + string.Join("/", filter);
            }

            public string[] Filter { get; init; }

            public bool Matches(string? other)
            {
                if (other == null) throw new ArgumentNullException(nameof(other));

                // Delegate to the configured matcher when available.
                // FullPattern holds the full subscription pattern (e.g. "#", "sensors/+/temp", "$DASHBOARD/#").
                // All wildcard semantics — including MQTT §4.7.2 '$' exclusion — are handled by the matcher.
                if (_matcher != null && FullPattern is TKey patternKey && other is TKey candidateKey)
                    return _matcher.Matches(patternKey, candidateKey);

                // Fallback: built-in MQTT-style matching for null matcher or non-string TKey.
                string path = Parent?.Path!;
                if (path == null) throw new InvalidOperationException($"Filter node '{KeyPart}' has null parent key");

                if (path.Length == 0)
                {
                    // MQTT 3.1.1 §4.7.2: wildcards at the root level must not match $-prefixed topics.
                    if (other.StartsWith('$'))
                        return false;

                    if (Filter.Length == 1 && Filter[0] == "#")
                        return true;
                    if (Filter.Length == 1 && Filter[0] == "+")
                        return !other.Contains("/");
                    if (Filter[0] != "+")
                        throw new InvalidOperationException($"Filter node '{KeyPart}' has invalid filter part '{Filter[0]}' - first part of filter array must be a wildcard +");
                }
                else
                {
                    if (!other.StartsWith(path) || (other.Length > path.Length && other[path.Length] != '/'))
                        return false;

                    if (other.Length == path.Length)
                        return Filter.Length == 1 && (Filter[0] == "#" || Filter[0] == "+");

                    other = other[(path.Length + 1)..];
                }

                string[] othera = other.Split('/');
                for (int i = 0; i < Filter.Length; i++)
                {
                    var filterPart = Filter[i];
                    if (filterPart == "#")
                    {
                        if (i >= othera.Length)
                            return false;
                        return true;
                    }
                    else if (filterPart == "+")
                    {
                        // matches any single part — continue
                    }
                    else
                    {
                        if (i >= othera.Length || filterPart != othera[i])
                            return false;
                    }
                }
                if (othera.Length > Filter.Length)
                    return Filter.Length > 0 && Filter[^1] == "#";
                return true;
            }

        }

        internal RootNode root = new();

        public struct Counts
        {
            public int SubCount;
            public int ItemCount;
            public int FilterCount;
        }

        public Counts GetCounts()
        {
            Counts ret;
            ret.SubCount = 0;
            ret.ItemCount = 0;
            ret.FilterCount = 0;

            var stack = new Stack<TreeNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.Sub != null)
                    ret.SubCount++;
                if (node is ItemNode itemNode)
                {
                    ret.ItemCount++;
                    foreach (var child in itemNode.Children)
                        stack.Push(child);
                    foreach (var filter in itemNode.Filters)
                        stack.Push(filter);
                }
                else if (node is FilterNode filterNode)
                {
                    ret.FilterCount++;
                }
            }
            return ret;
        }

        internal class CacheItemWithWildcards : CacheItem<TKey, TValue>
        {
            private readonly bool _isWildcard;
            // Set to true in OnRemove so that any queued async OnInvokeCallback tasks bail out immediately.
            private volatile bool _removed = false;

            public CacheItemWithWildcards(CacheWithWildcards<TKey, TValue> source, TKey key, bool retain, TreeNode node)
                : base(source, key, retain)
            {
                Node = node;
                _isWildcard = node is FilterNode;

                // Only take ownership of the node if it has no primary collection yet.
                // ConcurrentDictionary.GetOrAdd may invoke NewItem concurrently for the same key;
                // the throwaway duplicate must not overwrite the stored collection's node reference.
                // NewItem runs inside lock(root), so this check is safe.
                if (node.Sub == null)
                    node.Sub = this;
            }

            internal bool IsWildcard => _isWildcard;

            // Wildcard items must always run InitialInvokeAsync so it can walk the subtree
            // for existing matching items, even though the filter node itself has no value.
            internal override bool NeedsInitialInvoke => _isWildcard || !Status.IsPending;

            TreeNode Node { get; init; }

            // Per-key upstream value cache for wildcard subscriptions.
            // Solves the delivery race: upstream fires InitialInvokeAsync fire-and-forget before the
            // downstream subscriber is registered (NewItem runs inside _cache.GetOrAdd, before col.Add).
            // By caching here, InitialInvokeAsync can replay values to newly-registered subscribers
            // regardless of timing. Works for any ICache<TKey,TValue> upstream implementation.
            private readonly Dictionary<TKey, (TValue value, IStatus status)> _upstreamCache = new();
            private readonly object _upstreamCacheLock = new();

            internal async Task UpstreamCallbackWildcards(ISubscription<TKey, TValue> sub)
            {
                if (_isWildcard)
                {
                    // Each upstream callback carries a specific matching key (e.g. "sensors/temp").
                    // Concurrent callbacks for different keys must not race-overwrite each other through
                    // collection.Value, so we cache per-key and route directly to subscribers.
                    if (!sub.Status.IsPending)
                        lock (_upstreamCacheLock) { _upstreamCache[sub.Key] = (sub.Value, sub.Status); }
                    await InvokeCallback(sub);
                }
                else
                {
                    // Use PublishFromUpstreamAsync instead of PublishAsync to suppress the
                    // OnInvokeCallback tree walk. Any wildcard subscribers above this item in the
                    // tree (e.g. '#') have their own upstream subscriptions and will receive this
                    // value independently via the _isWildcard=true branch above (Path B).
                    // Allowing the tree walk (Path A) would cause duplicate delivery for every
                    // upstream publish when both an exact-key sub and a '#' sub exist.
                    await PublishFromUpstreamAsync(sub.Value, sub.Status);
                }
            }

            internal class SubscriptionCopy : ISubscription<TKey, TValue>
            {
                internal SubscriptionCopy(ISubscription<TKey, TValue> other)
                {
                    Key = other.Key;
                    Value = other.Value;
                    Status = other.Status;
                }
                public TKey Key { get; init; }

                public IStatus Status { get; init; }

                public TValue Value { get; init; }

                public void Dispose() { }
            }

            private sealed class CachedUpstreamValue : ISubscription<TKey, TValue>
            {
                internal CachedUpstreamValue(TKey key, TValue value, IStatus status)
                {
                    Key = key; Value = value; Status = status;
                }
                public TKey Key { get; }
                public TValue Value { get; }
                public IStatus Status { get; }
                public void Dispose() { }
            }

            internal override int MaxCallbackConcurrency
            {
                get
                {
                    if (Node is FilterNode)
                    {
                        // wildcard node so we need to allow unlimited concurrency as we dont know how many matches there will be when we fire the callback
                        return -1;
                    }
                    return base.MaxCallbackConcurrency;
                }
            }

            // this item has had an update and all subscriptions have been fired,
            // so now we need to check if we have any wildcard subscribers above us in the tree and fire them too
            protected override async Task OnInvokeCallback(CancellationToken cancellationToken = default)
            {
                // Bail out immediately if this item has already been removed.
                // Without this, hundreds of queued fire-and-forget tasks keep running after
                // unsubscription, each generating a diagnostic message per MQTT tick.
                if (_removed)
                    return;

                // if this is a wildcard node then dont look up the tree
                Debug.Assert(Node != null, $"Node is null");
                if (Node is FilterNode)
                    return;

                // Capture Sub locally — it can be nulled concurrently by OnRemove()
                var sub = Node.Sub;
                if (sub == null || sub.Key == null)
                {
                    Debug.WriteLine($"Node '{Node.KeyPart}' has null subscription or key — item was removed concurrently, skipping.");
                    return;
                }

                string? thisKey = sub.Key.ToString();

                int countCallbacks = 0;
                ItemNode? node = Node.Parent ?? throw new InvalidOperationException($"Filter node '{Node.KeyPart}' has null parent");
                while (node != null)
                {
                    // for each parent node up the tree...
                    List<TreeNode> filtersSnapshot;
                    lock (((CacheWithWildcards<TKey, TValue>)Source).root)
                    {
                        filtersSnapshot = node.Filters.ToList();
                    }

                    if (filtersSnapshot.Count > 0)
                    {
                        foreach (var filterNode in filtersSnapshot)
                        {
                            if (filterNode is FilterNode fn)
                            {
                                if (fn.Matches(thisKey))
                                {
                                    var fnsub = fn.Sub;
                                    if (fnsub == null)
                                    {
                                        Debug.WriteLine($"Filter node '{fn.KeyPart}' with filter '{string.Join('/', fn.Filter)}' has null subscription — item was removed concurrently, skipping.");
                                    }
                                    else
                                    {
                                        countCallbacks++;
                                        // fire it at wildcard subscriber...but with actual subsription as parameter
                                        // we need to protect this somehow...make it readonly or a clone
                                        var dummy = new SubscriptionCopy(sub);
                                        _ = fnsub.InvokeCallback(dummy, cancellationToken);
                                    }
                                }
                                else
                                {
                                    // not a match so do nothing
                                    // Debug.WriteLine($"Filter node '{fn.KeyPart}' with filter '{string.Join('/', fn.Filter)}' does not match key '{thisKey}'");
                                }
                            }
                        }
                    }
                    node = node.Parent;
                }
                if (countCallbacks == 0)
                {
                    // not very interesting...
                    // a node updated and no parent in the tree had a wilcard request
                    // Debug.WriteLine($"No filter nodes matched key '{thisKey}'");
                }
            }

            internal override async Task InitialInvokeAsync(Subscription<TKey, TValue> sub, CancellationToken cancellationToken = default)
            {
                // new sub added to an existing node
                // - so we need to fire the callback with the current value if there is one
                // - just defer to base
                if (Node is ItemNode)
                {
                    await base.InitialInvokeAsync(sub, cancellationToken);
                }
                else if (Node is FilterNode filterNode)
                {
                    if (UpstreamSub == null)
                    {
                        // No upstream sub — deliver values from the local item tree.
                        // This covers local-only caches (no upstream) or supportsWildcards: false.
                        ItemNode parent = Node.Parent ?? throw new InvalidOperationException($"Filter node '{Node.KeyPart}' has null parent");
                        var stack = new Stack<ItemNode>();
                        stack.Push(parent);
                        while (stack.Count > 0)
                        {
                            // for each node from parent downwards...
                            // - if it has a value
                            var node = stack.Pop();
                            if (node.Sub != null && !node.Sub.Status.IsPending)
                            {
                                if (filterNode.Matches(node.Sub.Key.ToString()))
                                {
                                    // fire it at wildcard subscriber...but with actual subsription as parameter
                                    // we need to protect this somehow...make it readonly or a clone
                                    var dummy = new SubscriptionCopy(node.Sub);
                                    _ = sub.InvokeCallback(dummy, cancellationToken);
                                }
                            }
                            if (node is ItemNode itemNode)
                            {
                                List<ItemNode> childrenSnapshot;
                                lock (((CacheWithWildcards<TKey, TValue>)Source).root)
                                {
                                    childrenSnapshot = itemNode.Children.ToList();
                                }
                                foreach (var child in childrenSnapshot)
                                    stack.Push(child);
                            }
                        }
                    }
                    else
                    {
                        // Upstream sub exists (supportsWildcards: true).
                        // Do NOT walk the local item tree. The upstream's own InitialInvokeAsync
                        // fires UpstreamCallbackWildcards for every existing upstream value and
                        // those callbacks reach this subscriber via one of two paths:
                        //   - Callbacks that arrived before this subscriber was registered populated
                        //     _upstreamCache; we replay those below.
                        //   - Callbacks that arrive after this subscriber is registered deliver
                        //     directly through InvokeCallback — no replay needed.
                        // Walking the tree as well would duplicate every delivery from the first path.
                        // Note: locally-published values that were not forwarded to upstream and did
                        // not arrive via an upstream callback will not be replayed. This is an
                        // accepted trade-off for a cache backed by a wildcard-capable upstream.
                        Dictionary<TKey, (TValue value, IStatus status)> snapshot;
                        lock (_upstreamCacheLock) { snapshot = new Dictionary<TKey, (TValue, IStatus)>(_upstreamCache); }

                        foreach (var (key, (value, status)) in snapshot)
                        {
                            var shim = new CachedUpstreamValue(key, value, status);
                            if (Source.WaitOnSubscriptionCallback)
                                await sub.InvokeCallback(shim, cancellationToken);
                            else
                                _ = sub.InvokeCallback(shim, cancellationToken);
                        }
                    }
                }
            }

            internal void OnRemove()
            {
                // Mark removed first so any in-flight OnInvokeCallback tasks bail out immediately.
                _removed = true;
                Node.Sub = null!;

                var rootNode = ((CacheWithWildcards<TKey, TValue>)Source).root;

                if (Node is FilterNode)
                {
                    // filter node so remove from parent filters
                    lock (rootNode)
                    {
                        Node.Parent?.Filters.Remove((TreeNode)Node);
                    }
                }
                else if (Node is ItemNode itemNode)
                {
                    // what if I have children? --leave this tree node simply without a subscription
                    lock (rootNode)
                    {
                        if (itemNode.Children.Count == 0)
                        {
                            if (itemNode.Parent == null)
                            {
                                Debug.WriteLine("Unexpected itemNode with null parent?");
                            }
                            else
                            {
                                if (itemNode.Parent.Children.Remove(itemNode) == false)
                                {
                                    Debug.WriteLine("Unexpected failure to remove itemNode from parent?");
                                }
                                //
                                // if parent is now empty and has no subscription then remove that too and so on up the tree?
                                // - this is just a cleanup to stop the tree growing indefinitely with empty nodes if we have lots of churn but the same keys coming and going
                                var parent = itemNode.Parent;
                                while (parent != null
                                    && object.ReferenceEquals(parent, rootNode) == false
                                    && parent.Sub == null
                                    && parent.Children.Count == 0
                                    && parent.Filters.Count == 0)
                                {
                                    if (parent.Parent == null)
                                    {
                                        Debug.WriteLine("Unexpected parent with null parent?");
                                        break;
                                    }
                                    else
                                    {
                                        var grandParent = parent.Parent;
                                        if (grandParent.Children.Remove(parent) == false)
                                        {
                                            Debug.WriteLine("Unexpected failure to remove parent itemNode from grandparent?");
                                            break;
                                        }
                                        parent = grandParent;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        internal override CacheItem<TKey, TValue> NewItem(TKey key)
        {
            //
            // split the key into parts and add it to the tree, navigating down from root
            // - we are adding new item so final node should not exist already
            // NOTE: ConcurrentDictionary.GetOrAdd may call this factory concurrently for the
            // same key. If another thread already added the key to the tree, return a throwaway
            // wrapper — GetOrAdd will discard it and return the first stored collection.
            //
            var parts = key.ToString()!.Split('/');
            ItemNode node = root;
            TreeNode? lastNode = null;
            //var isNew = false;
            var isWildcard = false;
            string[]? filter = null;

            // use root to lock tree of nodes whilst we find where to stick this one
            lock (root)
            {
                for (int p = 0; p < parts.Length; p++)
                {
                    var part = parts[p];
                    TKey key1 = part is TKey ? (TKey)(object)part : throw new InvalidOperationException($"Part '{part}' is not of type {typeof(TKey).FullName}");
                    isWildcard = part == "#" || part == "+";
                    if (isWildcard)
                    {
                        // remainder of parts array is now a filter
                        filter = parts[p..];

                        var filterNode = node.Filters.Find(c => c is FilterNode fn && fn.Filter.SequenceEqual(filter));
                        if (filterNode != null)
                        {
                            // Concurrent GetOrAdd: another thread already added this wildcard key.
                            // Return a throwaway wrapper; GetOrAdd will discard it.
                            lastNode = filterNode;
                            break;
                        }
                        filterNode = new FilterNode(key1, node, filter, _matcher);
                        node.Filters.Add(filterNode);
                        lastNode = filterNode;
                        break;
                    }
                    else
                    {
                        if (part.Contains('#') || part.Contains('+'))
                            throw new ValidationException($"Invalid key part '{part}' in key '{key}' - wildcard characters '#' and '+' are not allowed in key parts");

                        var child = node.Children.Find(c => c.KeyPart.Equals(part));
                        if (child != null)
                        {
                            node = child;
                            //isNew = false;
                        }
                        else
                        {

                            child = new ItemNode(key1, node);
                            node.Children.Add(child);
                            //isNew = true;
                        }
                        lastNode = node = child;
                    }
                }
                // If !isNew && !isWildcard: concurrent GetOrAdd called us twice for the same key.
                // Return a throwaway wrapper; ConcurrentDictionary will discard it.
                if (lastNode == null)
                    throw new InvalidOperationException($"Key '{key}' failed to set last node in tree search");

                var ret = new CacheItemWithWildcards(this, key, false, lastNode);

                return ret;
            }
        }

        internal override void RemoveItem(CacheItem<TKey, TValue> col)
        {
            var sct = col as CacheItemWithWildcards;
            if (sct == null)
                throw new InvalidOperationException("Expected CacheItemWithWildcards in RemoveItem");

            // Null out UpstreamSub before calling base.RemoveItem so the base doesn't double-unsubscribe.
            // We handle unsubscription here where we have the correctly-typed reference.
            if (Upstream != null && sct.UpstreamSub != null)
            {
                Upstream.Unsubscribe(sct.UpstreamSub);
                sct.UpstreamSub = null;
            }

            lock (root)
            {
                sct.OnRemove();
            }
            base.RemoveItem(col); // handles cache removal; upstream unsubscribe is skipped (UpstreamSub is null)
        }

        internal override void AttachUpstream(CacheItem<TKey, TValue> col)
        {
            if (Upstream == null || col.UpstreamSub != null) return;
            var item = (CacheItemWithWildcards)col;
            lock (col)
            {
                if (item.UpstreamSub != null) return;
                if (!item.IsWildcard || UpstreamSupportsWildcards)
                    item.UpstreamSub = Upstream.Subscribe(col.Key, item.UpstreamCallbackWildcards);
            }
        }

        /// <summary>
        /// Clears the cache dictionary AND resets all tree node references so that
        /// <see cref="NewItem"/> can safely re-create collections for the same keys.
        /// </summary>
        public override void Clear()
        {
            lock (root)
            {
                ResetTreeNodes(root);
            }
            base.Clear();
        }

        private static void ResetTreeNodes(ItemNode node)
        {
            node.Sub = null!;
            foreach (var child in node.Children)
                ResetTreeNodes(child);
            foreach (var filter in node.Filters)
                filter.Sub = null!;
        }

    }
}

