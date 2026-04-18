using System.Text;
using System.Text.Json;

namespace PSTT.Remote.Protocol
{
    /// <summary>
    /// Encode/decode helpers for <see cref="RemoteMessage"/> over any transport.
    /// Messages are serialised as UTF-8 JSON; TValue payloads are embedded as Base64.
    /// </summary>
    public static class RemoteProtocol
    {
        private static readonly JsonSerializerOptions _options = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };

        public static byte[] Encode(RemoteMessage message)
            => JsonSerializer.SerializeToUtf8Bytes(message, _options);

        public static RemoteMessage? Decode(ReadOnlyMemory<byte> data)
        {
            try
            {
                return JsonSerializer.Deserialize<RemoteMessage>(data.Span, _options);
            }
            catch
            {
                return null;
            }
        }

        // ── Convenience factory methods ───────────────────────────────────────

        public static RemoteMessage Subscribe(string key)
            => new() { Type = RemoteMessageTypes.Subscribe, Key = key };

        public static RemoteMessage Unsubscribe(string key)
            => new() { Type = RemoteMessageTypes.Unsubscribe, Key = key };

        public static RemoteMessage Publish(string key, byte[] payload, bool retain)
            => new()
            {
                Type    = RemoteMessageTypes.Publish,
                Key     = key,
                Payload = Convert.ToBase64String(payload),
                Retain  = retain,
            };

        public static RemoteMessage Update(string key, byte[] payload, PSTT.Data.IStatus status)
            => new()
            {
                Type          = RemoteMessageTypes.Update,
                Key           = key,
                Payload       = Convert.ToBase64String(payload),
                StatusState   = (int)status.State,
                StatusMessage = status.Message,
                StatusCode    = status.Code,
            };

        public static byte[]? DecodePayload(RemoteMessage message)
        {
            if (message.Payload == null) return null;
            try { return Convert.FromBase64String(message.Payload); }
            catch { return null; }
        }
    }
}
