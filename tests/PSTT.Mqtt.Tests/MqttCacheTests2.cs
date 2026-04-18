using PSTT.Data;
using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Text;

namespace PSTT.Mqtt.Tests
{
    public class MqttCacheTests2
    {
        /// <summary>Poll condition every 20 ms for up to timeoutMs before failing.</summary>
        private static async Task<bool> WaitForConditionAsync(Func<bool> condition, int timeoutMs = 3000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(20);
            }
            return false;
        }

        [Fact]
        public async Task BrokerAndTwoDataSourceClients()
        {
            await using var broker = await FakeBroker.StartAsync();

            await using var ds1 = new MqttCache<string>(
                "127.0.0.1",
                broker.Port,
                bytes => Encoding.UTF8.GetString(bytes),
                value => Encoding.UTF8.GetBytes(value));
            await ds1.ConnectAsync();

            await using var ds2 = new MqttCache<string>(
                "127.0.0.1",
                broker.Port,
                bytes => Encoding.UTF8.GetString(bytes),
                value => Encoding.UTF8.GetBytes(value));
            await ds2.ConnectAsync();

            // Publish to topic1 with ds1,
            await ds1.PublishAsync("topic1", "hello", null, true);

            // Subscribe to topic1 with ds2,
            var recv = "";
            var subscription = ds2.Subscribe("topic1", async (s) => recv = s.Value);

            // Wait for the message to be received.
            await WaitForConditionAsync(() => recv != "");
            Assert.Equal("hello", recv);
        }

        [Fact]
        public async Task BrokerAndTwoDataSourceClients_Builder()
        {
            await using var broker = await FakeBroker.StartAsync();

            await using var ds1 = new MqttCacheBuilder<string>()
                                .WithBroker("127.0.0.1", broker.Port)
                                .WithUtf8Encoding()
                                .Build();
            await ds1.ConnectAsync();

            await using var ds2 = new MqttCacheBuilder<string>()
                                .WithBroker("127.0.0.1", broker.Port)
                                .WithUtf8Encoding()
                                .Build();
            await ds2.ConnectAsync();

            // Publish to topic1 with ds1,
            await ds1.PublishAsync("topic1", "hello", null, true);

            // Subscribe to topic1 with ds2,
            var recv = "";
            var subscription = ds2.Subscribe("topic1", async (s) => recv = s.Value);

            // Wait for the message to be received.
            await WaitForConditionAsync(() => recv != "");
            Assert.Equal("hello", recv);
        }

        [Fact]
        public async Task BrokerAndTwoDataSourceClientsAndDownstreams()
        {
            await using var broker = await FakeBroker.StartAsync();

            await using var ds1 = new MqttCacheBuilder<string>()
                                .WithBroker("127.0.0.1", broker.Port)
                                .WithUtf8Encoding()
                                .Build();
            await ds1.ConnectAsync();

            var ds1a = new CacheBuilder<string, string>()
                    .WithWildcards()
                    .WithUpstream(ds1, supportsWildcards: true, forwardPublish: true)
                               .Build();

            await using var ds2 = new MqttCacheBuilder<string>()
                                .WithBroker("127.0.0.1", broker.Port)
                                .WithUtf8Encoding()
                                .Build();
            await ds2.ConnectAsync();

            var ds2a = new CacheBuilder<string, string>()
                    .WithWildcards()
                    .WithUpstream(ds2, supportsWildcards: true, forwardPublish: true)
                    .Build();

            // Publish to topic1 with ds1a,
            await ds1a.PublishAsync("topic1", "hello", null, true);

            // Subscribe to topic1 with ds2a,
            var recv = "";
            var subscription = ds2a.Subscribe("topic1", async (s) => recv = s.Value);

            // Wait for the message to be received.
            await WaitForConditionAsync(() => recv != "");
            Assert.Equal("hello", recv);
        }

        [Fact]
        public async Task ByteArray_RawEncoding()
        {
            await using var broker = await FakeBroker.StartAsync();

            await using var ds1 = new MqttCacheBuilder<byte[]>()
                                .WithBroker("127.0.0.1", broker.Port)
                                .WithRawEncoding()
                                .Build();
            await ds1.ConnectAsync();

            var ds1a = new CacheBuilder<string, byte[]>()
                    .WithWildcards()
                    .WithUpstream(ds1, supportsWildcards: true, forwardPublish: true)
                               .Build();

            await using var ds2 = new MqttCacheBuilder<byte[]>()
                                .WithBroker("127.0.0.1", broker.Port)
                                .WithRawEncoding()
                                .Build();
            await ds2.ConnectAsync();

            var ds2a = new CacheBuilder<string, byte[]>()
                    .WithWildcards()
                    .WithUpstream(ds2, supportsWildcards: true, forwardPublish: true)
                    .Build();

            // helloXXX in bytes
            byte[] ba = new byte[] { 104, 101, 108, 108, 111, 88, 88, 88 };
            // Publish to topic1 with ds1a,
            await ds1a.PublishAsync("topic1", ba, null, true);

            // Subscribe to topic1 with ds2a,
            byte[]? recv = null;
            var subscription = ds2a.Subscribe("topic1", async (s) => recv = s.Value);

            // Wait for the message to be received.
            await WaitForConditionAsync(() => recv != null);
            Assert.NotNull(recv);
            Assert.Equal("helloXXX", Encoding.UTF8.GetString(recv));
        }

        struct Data
        {
            public string? Text { get; set; }
            public int Number { get; set; }
        };

        [Fact]
        public async Task StructData_JsonEncoding()
        {
            await using var broker = await FakeBroker.StartAsync();

            await using var ds1 = new MqttCacheBuilder<Data>()
                                .WithBroker("127.0.0.1", broker.Port)
                                .WithJsonEncoding()
                                .Build();
            await ds1.ConnectAsync();

            var ds1a = new CacheBuilder<string, Data>()
                    .WithWildcards()
                    .WithUpstream(ds1, supportsWildcards: true, forwardPublish: true)
                               .Build();

            await using var ds2 = new MqttCacheBuilder<Data>()
                                        .WithBroker("127.0.0.1", broker.Port)
                                        .WithJsonEncoding()
                                        .Build();
            await ds2.ConnectAsync();

            var ds2a = new CacheBuilder<string, Data>()
                    .WithWildcards()
                    .WithUpstream(ds2, supportsWildcards: true, forwardPublish: true)
                    .Build();

            // data
            Data d = new() { Text = "hello", Number = 42 };

            // Publish to topic1 with ds1a,
            await ds1a.PublishAsync("topic1", d, null, true);

            // Subscribe to topic1 with ds2a,
            Data? recv = null;
            int updates = 0;
            var subscription = ds2a.Subscribe("topic1", async (s) =>
            {
                recv = s.Value;
                updates++;
            });

            // Wait for the message to be received.
            await WaitForConditionAsync(() => recv != null);
            Assert.NotNull(recv);
            Assert.Equal("hello", recv.Value.Text);
            Assert.Equal(42, recv.Value.Number);
            Assert.Equal(1, updates);

            // Update to topic1 with ds1a,
            d.Text += " world";
            d.Number++;
            await ds1a.PublishAsync("topic1", d, null, true);

            // Wait for the message to be received.
            await WaitForConditionAsync(() => updates > 1);
            Assert.NotNull(recv);
            Assert.Equal("hello world", recv.Value.Text);
            Assert.Equal(43, recv.Value.Number);
            Assert.Equal(2, updates);

            // Wait in case there's some further message received by mistake (there shouldn't be).
            await WaitForConditionAsync(() => updates != 2, 500);
            Assert.Equal(2, updates);

        }

        [Fact]
        public async Task StructData_JsonEncoding_Wildcard()
        {
            await using var broker = await FakeBroker.StartAsync();

            await using var ds1 = new MqttCacheBuilder<Data>()
                                .WithBroker("127.0.0.1", broker.Port)
                                .WithJsonEncoding()
                                .Build();
            await ds1.ConnectAsync();

            var ds1a = new CacheBuilder<string, Data>()
                    .WithWildcards()
                    .WithUpstream(ds1, supportsWildcards: true, forwardPublish: true)
                               .Build();

            await using var ds2 = new MqttCacheBuilder<Data>()
                                        .WithBroker("127.0.0.1", broker.Port)
                                        .WithJsonEncoding()
                                        .Build();
            await ds2.ConnectAsync();

            var ds2a = new CacheBuilder<string, Data>()
                    .WithWildcards()
                    .WithUpstream(ds2, supportsWildcards: true, forwardPublish: true)
                    .Build();

            // data
            Data d = new() { Text = "hello", Number = 42 };

            // Publish to topic1 with ds1a,
            await ds1a.PublishAsync("topic1", d, null, true);

            // Subscribe to topic1 with ds2a,
            Data? recv = null;
            string keyReceived = "";
            int updates = 0;
            var subscription = ds2a.Subscribe("#", async (s) =>
            {
                recv = s.Value;
                keyReceived = s.Key;
                updates++;
            });

            // Wait for the message to be received.
            await WaitForConditionAsync(() => recv != null);
            Assert.NotNull(recv);
            Assert.Equal("hello", recv.Value.Text);
            Assert.Equal(42, recv.Value.Number);
            Assert.Equal("topic1", keyReceived);
            Assert.Equal(1, updates);

            // Update to topic1 with ds1a,
            d.Text += " world";
            d.Number++;
            await ds1a.PublishAsync("topic1", d, null, true);

            // Wait for the message to be received.
            await WaitForConditionAsync(() => updates > 1);
            Assert.NotNull(recv);
            Assert.Equal("hello world", recv.Value.Text);
            Assert.Equal(43, recv.Value.Number);
            Assert.Equal(2, updates);

            // Wait in case there's some further message received by mistake (there shouldn't be).
            await WaitForConditionAsync(() => updates != 2, 500);
            Assert.Equal(2, updates);

        }
    }
}
