using System.Buffers.Binary;
using System.Text.Json;
using FocusRemote.Protocol.Messages.Models.Control;

namespace FocusRemote.Protocol.Messages;

/// <summary>Control channel payload codec: JSON (UTF-8, camelCase) plus the binary Ping/Pong payload.</summary>
public static class ControlMessages
{
    /// <summary>Shared options: camelCase names, case-insensitive read, unknown fields ignored.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public static byte[] SerializeJson<T>(T message) => JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);

    /// <summary>Deserializes a JSON payload; false (never throws) on malformed JSON or missing required fields.</summary>
    public static bool TryDeserializeJson<T>(ReadOnlySpan<byte> payload, out T? message) where T : class
    {
        try
        {
            message = JsonSerializer.Deserialize<T>(payload, JsonOptions);
            return message is not null;
        }
        catch (JsonException)
        {
            message = null;
            return false;
        }
    }

    /// <summary>Ping/Pong payload: sender timestamp in ms as u64 LE.</summary>
    public static byte[] BuildPing(ulong timestampMs)
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, timestampMs);
        return payload;
    }

    public static bool TryParsePing(ReadOnlySpan<byte> payload, out ulong timestampMs)
    {
        timestampMs = 0;
        if (payload.Length != 8)
            return false;
        timestampMs = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        return true;
    }

    public static byte[] SerializeEmpty() => SerializeJson(EmptyMessage.Instance);
}
