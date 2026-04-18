namespace PSTT.Remote.Transport.SignalR
{
    /// <summary>
    /// Shared method-name constants used by both <see cref="SignalRClientTransport"/>
    /// (client side) and <c>CacheHub</c> / <c>SignalRConnectionTransport</c>
    /// (server side in PSTT.Remote.AspNetCore).
    /// </summary>
    public static class SignalRProtocol
    {
        /// <summary>Hub method name invoked by the client to send data to the server.</summary>
        public const string ClientToServerMethod = "Send";

        /// <summary>Hub method name invoked by the server to push data to a client.</summary>
        public const string ServerToClientMethod = "Receive";
    }
}
