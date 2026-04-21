using PSTT.Data;
using PSTT.Remote;
using PSTT.Remote.AspNetCore.Extensions;
using PSTT.Remote.AspNetCore.SignalR;
using PSTT.Remote.AspNetCore.WebSocket;
using PSTT.Remote.Transport.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;

namespace PSTT.Remote.AspNetCore.Tests
{
    /// <summary>
    /// Integration tests for PSTT.Remote.AspNetCore — SignalR and WebSocket transports
    /// running through an in-memory ASP.NET Core TestServer.
    ///
    /// Test groups:
    ///   1. SignalR — client connects via HubConnection backed by TestServer
    ///   2. WebSocket (ASP.NET) — client connects via WebSocketClientTransport
    /// </summary>
    public class AspNetCoreTransportTests : IAsyncDisposable
    {
        // ── Helpers ───────────────────────────────────────────────────────────

        private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(20);
            }
            return false;
        }

        // ── Upstream shared for all tests ─────────────────────────────────────
        public async ValueTask DisposeAsync() { await Task.CompletedTask; }

        // ────────────────────────────────────────────────────────────────────
        // Group 1: SignalR transport
        // ────────────────────────────────────────────────────────────────────

        /// Builds an in-memory ASP.NET Core TestServer hosting CacheHub.
        private static (WebApplication app, CacheWithWildcards<string, string> upstream)
            BuildSignalRTestApp()
        {
            var upstream = new CacheWithWildcards<string, string>();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();

            builder.Services.AddCacheSignalRServer<string>(
                upstream,
                s => Encoding.UTF8.GetBytes(s),
                b => Encoding.UTF8.GetString(b),
                forwardPublish: true);

            var app = builder.Build();
            app.MapCacheHub("/datasource");
            return (app, upstream);
        }

        private static HubConnection BuildSignalRHubConnection(WebApplication app)
        {
            var testServer = app.GetTestServer();
            return new HubConnectionBuilder()
                .WithUrl("http://localhost/datasource",
                    o => o.HttpMessageHandlerFactory = _ => testServer.CreateHandler())
                .Build();
        }

        [Fact]
        public async Task SignalR_Subscribe_ReceivesUpstreamPublish()
        {
            var (app, upstream) = BuildSignalRTestApp();
            await app.StartAsync();
            try
            {
                var hub = BuildSignalRHubConnection(app);
                await using var client = new RemoteCacheBuilder<string>()
                    .WithSignalRTransport(hub)
                    .WithUtf8Encoding()
                    .Build();

                await client.ConnectAsync();

                string? received = null;
                var sub = client.Subscribe("signalr/temp", async s => { received = s.Value; });
                await Task.Delay(100);

                await upstream.PublishAsync("signalr/temp", "42");

                Assert.True(await WaitForAsync(() => received == "42"),
                    "SignalR client did not receive upstream publish");

                client.Unsubscribe(sub);
            }
            finally { await app.StopAsync(); }
        }

        [Fact]
        public async Task SignalR_Wildcard_ReceivesMultipleTopics()
        {
            var (app, upstream) = BuildSignalRTestApp();
            await app.StartAsync();
            try
            {
                var hub = BuildSignalRHubConnection(app);
                await using var client = new RemoteCacheBuilder<string>()
                    .WithSignalRTransport(hub)
                    .WithUtf8Encoding()
                    .Build();

                await client.ConnectAsync();

                var keys = new List<string>();
                var sub = client.Subscribe("devices/+/state", async s =>
                {
                    lock (keys) keys.Add(s.Key);
                });
                await Task.Delay(100);

                await upstream.PublishAsync("devices/a/state", "on");
                await upstream.PublishAsync("devices/b/state", "off");

                Assert.True(await WaitForAsync(() => { lock (keys) return keys.Count >= 2; }),
                    $"Expected 2 wildcard messages via SignalR, got {keys.Count}");

                client.Unsubscribe(sub);
            }
            finally { await app.StopAsync(); }
        }

        [Fact]
        public async Task SignalR_Publish_ForwardedToUpstream()
        {
            var (app, upstream) = BuildSignalRTestApp();
            await app.StartAsync();
            try
            {
                var hub = BuildSignalRHubConnection(app);
                await using var client = new RemoteCacheBuilder<string>()
                    .WithSignalRTransport(hub)
                    .WithUtf8Encoding()
                    .Build();

                await client.ConnectAsync();
                await Task.Delay(100);

                string? upstreamReceived = null;
                upstream.Subscribe("signalr/cmd", async s =>
                {
                    if (!s.Status.IsPending) upstreamReceived = s.Value;
                });

                await client.PublishAsync("signalr/cmd", "PING");

                Assert.True(await WaitForAsync(() => upstreamReceived == "PING"),
                    "SignalR client publish was not forwarded to upstream");
            }
            finally { await app.StopAsync(); }
        }

        [Fact]
        public async Task SignalR_MultipleClients_BothReceive()
        {
            var (app, upstream) = BuildSignalRTestApp();
            await app.StartAsync();
            try
            {
                var hub1 = BuildSignalRHubConnection(app);
                var hub2 = BuildSignalRHubConnection(app);

                await using var c1 = new RemoteCacheBuilder<string>()
                    .WithSignalRTransport(hub1).WithUtf8Encoding().Build();
                await using var c2 = new RemoteCacheBuilder<string>()
                    .WithSignalRTransport(hub2).WithUtf8Encoding().Build();

                // Subscribe BEFORE ConnectAsync so ResubscribeAllAsync awaits each sub
                // round-trip — guaranteeing server-side registration before publishing.
                string? r1 = null, r2 = null;
                var s1 = c1.Subscribe("signalr/shared", async s => { r1 = s.Value; });
                var s2 = c2.Subscribe("signalr/shared", async s => { r2 = s.Value; });

                await c1.ConnectAsync();
                await c2.ConnectAsync();

                await upstream.PublishAsync("signalr/shared", "hello-all");

                Assert.True(await WaitForAsync(() => r1 == "hello-all" && r2 == "hello-all"),
                    $"Not all SignalR clients received: r1={r1}, r2={r2}");

                c1.Unsubscribe(s1);
                c2.Unsubscribe(s2);
            }
            finally { await app.StopAsync(); }
        }

        [Fact]
        public async Task SignalR_Disconnect_TopicsMarkedStale()
        {
            var (app, upstream) = BuildSignalRTestApp();
            await app.StartAsync();
            try
            {
                var hub = BuildSignalRHubConnection(app);
                await using var client = new RemoteCacheBuilder<string>()
                    .WithSignalRTransport(hub)
                    .WithUtf8Encoding()
                    .Build();

                await client.ConnectAsync();

                PSTT.Data.IStatus? lastStatus = null;
                var sub = client.Subscribe("signalr/live", async s => { lastStatus = s.Status; });
                await Task.Delay(100);

                await upstream.PublishAsync("signalr/live", "alive");
                Assert.True(await WaitForAsync(() => lastStatus?.IsActive == true));

                await client.DisconnectAsync();

                Assert.True(await WaitForAsync(() =>
                    lastStatus?.State == PSTT.Data.IStatus.StateValue.Stale),
                    $"Expected Stale after disconnect, got {lastStatus?.State}");
            }
            finally { await app.StopAsync(); }
        }

        // ────────────────────────────────────────────────────────────────────
        // Group 2: AspNetCore WebSocket transport
        // ────────────────────────────────────────────────────────────────────

        private static (WebApplication app, CacheWithWildcards<string, string> upstream, int port)
            BuildWebSocketTestApp()
        {
            var upstream = new CacheWithWildcards<string, string>();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();

            builder.Services.AddCacheWebSocketServer<string>(
                upstream,
                s => Encoding.UTF8.GetBytes(s),
                b => Encoding.UTF8.GetString(b),
                forwardPublish: true);

            var app = builder.Build();
            app.UseWebSockets();
            app.MapCacheWebSocket("/dsws");
            return (app, upstream, 0);
        }

        private static WebSocketClientTransport BuildWsClient(WebApplication app)
        {
            // Not used in final tests — direct WebSocketConnectionTransport is used instead
            throw new NotImplementedException();
        }

        [Fact]
        public async Task AspNetCoreWebSocket_Subscribe_ReceivesUpstreamPublish()
        {
            var (app, upstream, _) = BuildWebSocketTestApp();
            await app.StartAsync();
            try
            {
                var testServer = app.GetTestServer();
                var ws = await testServer.CreateWebSocketClient()
                    .ConnectAsync(new Uri("ws://localhost/dsws"), CancellationToken.None);

                var transport = new WebSocketConnectionTransport(ws);
                await using var client = new RemoteCacheBuilder<string>()
                    .WithTransport(transport)
                    .WithUtf8Encoding()
                    .Build();

                // ConnectAsync is no-op (transport already connected), just start receive loop
                transport.StartReceiving();
                await client.ConnectAsync();

                string? received = null;
                var sub = client.Subscribe("aspws/topic", async s => { received = s.Value; });
                await Task.Delay(100);

                await upstream.PublishAsync("aspws/topic", "hello-ws");

                Assert.True(await WaitForAsync(() => received == "hello-ws"),
                    "AspNetCore WebSocket client did not receive upstream publish");

                client.Unsubscribe(sub);
            }
            finally { await app.StopAsync(); }
        }

        [Fact]
        public async Task AspNetCoreWebSocket_Publish_ForwardedToUpstream()
        {
            var (app, upstream, _) = BuildWebSocketTestApp();
            await app.StartAsync();
            try
            {
                var testServer = app.GetTestServer();
                var ws = await testServer.CreateWebSocketClient()
                    .ConnectAsync(new Uri("ws://localhost/dsws"), CancellationToken.None);

                var transport = new WebSocketConnectionTransport(ws);
                await using var client = new RemoteCacheBuilder<string>()
                    .WithTransport(transport)
                    .WithUtf8Encoding()
                    .Build();

                transport.StartReceiving();
                await client.ConnectAsync();
                await Task.Delay(100);

                string? upstreamReceived = null;
                upstream.Subscribe("aspws/cmd", async s =>
                {
                    if (!s.Status.IsPending) upstreamReceived = s.Value;
                });

                await client.PublishAsync("aspws/cmd", "GO");

                Assert.True(await WaitForAsync(() => upstreamReceived == "GO"),
                    "AspNetCore WebSocket publish not forwarded to upstream");
            }
            finally { await app.StopAsync(); }
        }

        // ────────────────────────────────────────────────────────────────────
        // Group 3: Validation / constructor tests
        // ────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task SignalRServerTransport_ClientConnected_Fires_OnConnection()
        {
            var transport = new SignalRServerTransport();
            PSTT.Remote.Transport.IRemoteTransport? fired = null;
            transport.ClientConnected += t => { fired = t; return Task.CompletedTask; };

            var mockCaller = new MockSingleClientProxy();
            await transport.OnClientConnectedAsync("conn1", mockCaller);

            Assert.NotNull(fired);
        }

        [Fact]
        public async Task SignalRServerTransport_Disconnect_TriggersDisconnectedOnConnectionTransport()
        {
            var transport = new SignalRServerTransport();
            bool disconnected = false;

            transport.ClientConnected += t =>
            {
                t.Disconnected += () => { disconnected = true; return Task.CompletedTask; };
                return Task.CompletedTask;
            };

            var mockCaller = new MockSingleClientProxy();
            await transport.OnClientConnectedAsync("conn-dc", mockCaller);
            await transport.OnClientDisconnectedAsync("conn-dc");

            Assert.True(disconnected);
        }

        [Fact]
        public void CacheHub_Constructs_WithValidTransport()
        {
            var transport = new SignalRServerTransport();
            var hub = new CacheHub(transport);
            Assert.NotNull(hub);
        }

        // ────────────────────────────────────────────────────────────────────
        // Helpers
        // ────────────────────────────────────────────────────────────────────

        private sealed class MockSingleClientProxy : Microsoft.AspNetCore.SignalR.ISingleClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args,
                CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<T> InvokeCoreAsync<T>(string method, object?[] args,
                CancellationToken cancellationToken = default) => Task.FromResult(default(T)!);
        }
    }
}
