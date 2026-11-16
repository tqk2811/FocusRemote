using System.Buffers.Binary;
using FocusRemote.Protocol.Messages.Models.Terminal;

namespace FocusRemote.Protocol.Messages;

/// <summary>
/// Terminal channel payload codec (protocol §5.5). ListShells/ShellList/Open/Opened/Closed are JSON
/// (<see cref="ControlMessages.SerializeJson"/>); Input/Output/Resize/Close are binary and start with the terminal id (u32 LE).
/// </summary>
public static class TerminalMessages
{
    public const int TerminalIdLength = 4;

    public static byte[] BuildData(uint terminalId, ReadOnlySpan<byte> data)
    {
        byte[] payload = new byte[TerminalIdLength + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, terminalId);
        data.CopyTo(payload.AsSpan(TerminalIdLength));
        return payload;
    }

    /// <summary><see cref="TerminalDataMessage.Data"/> slices <paramref name="payload"/> (no copy).</summary>
    public static bool TryParseData(byte[] payload, out TerminalDataMessage? message)
    {
        message = null;
        if (payload is null || payload.Length < TerminalIdLength)
            return false;
        message = new TerminalDataMessage
        {
            TerminalId = BinaryPrimitives.ReadUInt32LittleEndian(payload),
            Data = payload.AsMemory(TerminalIdLength),
        };
        return true;
    }

    public static byte[] BuildResize(uint terminalId, ushort cols, ushort rows)
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, terminalId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), cols);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), rows);
        return payload;
    }

    /// <summary>False when the payload is short or a dimension is 0.</summary>
    public static bool TryParseResize(ReadOnlySpan<byte> payload, out TerminalResizeMessage? message)
    {
        message = null;
        if (payload.Length < 8)
            return false;
        ushort cols = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        ushort rows = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
        if (cols == 0 || rows == 0)
            return false;
        message = new TerminalResizeMessage { TerminalId = BinaryPrimitives.ReadUInt32LittleEndian(payload), Cols = cols, Rows = rows };
        return true;
    }

    public static byte[] BuildClose(uint terminalId)
    {
        byte[] payload = new byte[TerminalIdLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, terminalId);
        return payload;
    }

    public static bool TryParseClose(ReadOnlySpan<byte> payload, out uint terminalId)
    {
        terminalId = 0;
        if (payload.Length < TerminalIdLength)
            return false;
        terminalId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        return true;
    }
}
