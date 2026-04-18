using System.Text.Json.Serialization;

namespace PSTT.Remote.Protocol
{
    /// <summary>
    /// All messages exchanged between <see cref="RemoteCache{TValue}"/> and
    /// <see cref="RemoteCacheServer{TValue}"/> over any transport.
    ///
    /// Direction key:
    ///   sub   — client → server: subscribe to a key/wildcard
    ///   unsub — client → server: unsubscribe from a key/wildcard
    ///   pub   — client → server: publish a value (requires forwardPublish on server)
    ///   upd   — server → client: value/status update for a subscribed key
    /// </summary>
    public sealed class RemoteMessage
    {
        /// <summary>Message type: "sub" | "unsub" | "pub" | "upd"</summary>
        [JsonPropertyName("t")]
        public string Type { get; set; } = string.Empty;

        /// <summary>Topic / key string.</summary>
        [JsonPropertyName("k")]
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Base-64–encoded TValue payload bytes. Present for "pub" and "upd" messages.
        /// Null for "sub" and "unsub".
        /// </summary>
        [JsonPropertyName("p")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Payload { get; set; }

        /// <summary>Retain flag — only meaningful for "pub" messages.</summary>
        [JsonPropertyName("r")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Retain { get; set; }

        // ── Status fields — only present on "upd" messages ────────────────

        [JsonPropertyName("ss")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int StatusState { get; set; }

        [JsonPropertyName("sm")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StatusMessage { get; set; }

        [JsonPropertyName("sc")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int StatusCode { get; set; }
    }

    public static class RemoteMessageTypes
    {
        public const string Subscribe   = "sub";
        public const string Unsubscribe = "unsub";
        public const string Publish     = "pub";
        public const string Update      = "upd";
    }
}
