using System;
using System.Collections.Generic;
using System.Text;

namespace PSTT.Data.Tests
{
    public class OtherTests
    {
        // Publish new item to a CacheWithWildcards and with an upstream set
        // - that cache seems to make a subscribe to the upstream which it shouldnt
        // - it was becuase NewItem included a callto upstream subscribe
        // - but that call shold be outside of NewItem and only done on actual subscribe to this cache
        [Fact]
        public async Task TestPublishToCacheWithWildcardsAndUpstream()
        {
            var upstream = new CacheWithWildcards<string, string>(new CacheConfig<string, string>() { Dispatcher = new SynchronousDispatcher() } );
            var cache = new CacheWithWildcards<string, string>(new CacheConfig<string, string>() { Dispatcher = new SynchronousDispatcher() } );
            cache.SetUpstream(upstream, true, true);

            await cache.PublishAsync("key1", "value1", null, true);

            Assert.Equal(1, cache.Count);
            Assert.Equal(0, cache.SubscribeCount);

            // upstream should have 1 item (from the forwarded publish) but no subscriptions (since the publish shouldnt cause a subscribe)
            Assert.Equal(1, upstream.Count);
            Assert.Equal(0, upstream.SubscribeCount);
        }
    }
}
