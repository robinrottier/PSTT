using PSTT.Data;
using PSTT.Remote;
using PSTT.Remote.AspNetCore.SignalR;
using PSTT.Remote.AspNetCore.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace PSTT.Remote.AspNetCore.Extensions
{
    /// <summary>
    /// Extension methods for registering DataSource remote server infrastructure in an
    /// ASP.NET Core DI container and mapping hub/WebSocket endpoints.
    /// </summary>
    public static class CacheServiceCollectionExtensions
    {
        // ── SignalR ────────────────────────────────────────────────────────────

        /// <summary>
        /// Register a <see cref="RemoteCacheServer{TValue}"/> backed by SignalR.
        /// Also registers <see cref="SignalRServerTransport"/> and calls
        /// <c>AddSignalR()</c>.  Map the hub endpoint with
        /// <see cref="MapCacheHub(IEndpointRouteBuilder, string)"/>.
        /// </summary>
        public static IServiceCollection AddCacheSignalRServer<TValue>(
            this IServiceCollection services,
            ICache<string, TValue> upstream,
            Func<TValue, byte[]> serializer,
            Func<byte[], TValue> deserializer,
            bool forwardPublish = false)
        {
            var transport = new SignalRServerTransport();
            var server = new RemoteCacheServer<TValue>(
                upstream, serializer, deserializer, transport, forwardPublish);
            _ = server.StartAsync();

            services.AddSingleton(transport);
            services.AddSingleton(server);
            services.AddSignalR();
            return services;
        }

        /// <summary>
        /// Map <see cref="CacheHub"/> to <paramref name="pattern"/>.
        /// Must be called on an <see cref="IEndpointRouteBuilder"/> (e.g. from
        /// <c>app.MapCacheHub()</c> after <c>app.Build()</c>).
        /// </summary>
        public static IEndpointRouteBuilder MapCacheHub(
            this IEndpointRouteBuilder endpoints,
            string pattern = "/datasource")
        {
            endpoints.MapHub<CacheHub>(pattern);
            return endpoints;
        }

        // ── WebSocket ──────────────────────────────────────────────────────────

        /// <summary>
        /// Register a <see cref="RemoteCacheServer{TValue}"/> backed by an
        /// ASP.NET Core WebSocket transport.
        /// Map the WebSocket endpoint with
        /// <see cref="MapCacheWebSocket(IEndpointRouteBuilder, string)"/>
        /// and ensure <c>app.UseWebSockets()</c> is called before the endpoint.
        /// </summary>
        public static IServiceCollection AddCacheWebSocketServer<TValue>(
            this IServiceCollection services,
            ICache<string, TValue> upstream,
            Func<TValue, byte[]> serializer,
            Func<byte[], TValue> deserializer,
            bool forwardPublish = false)
        {
            var transport = new AspNetCoreWebSocketServerTransport();
            var server = new RemoteCacheServer<TValue>(
                upstream, serializer, deserializer, transport, forwardPublish);
            _ = server.StartAsync();

            services.AddSingleton(transport);
            services.AddSingleton(server);
            return services;
        }

        /// <summary>
        /// Map a WebSocket endpoint that hands connections to
        /// <see cref="AspNetCoreWebSocketServerTransport"/>.
        /// </summary>
        public static IEndpointRouteBuilder MapCacheWebSocket(
            this IEndpointRouteBuilder endpoints,
            string pattern = "/datasource/ws")
        {
            endpoints.Map(pattern, async context =>
            {
                var wsTransport = context.RequestServices
                    .GetRequiredService<AspNetCoreWebSocketServerTransport>();
                await wsTransport.AcceptAsync(context);
            });
            return endpoints;
        }
    }
}
