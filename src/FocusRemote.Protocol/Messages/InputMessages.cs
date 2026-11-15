using System.Buffers.Binary;
using System.Text;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Messages.Models.Input;

namespace FocusRemote.Protocol.Messages;

/// <summary>Input channel payload codec (protocol §5.3).</summary>
public static class InputMessages
{
    /// <summary>Builds the payload (without channel/type bytes); seal with <see cref="InputMessage.Type"/>.</summary>
    public static byte[] Build(InputMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message)
        {
            case MouseMoveInput m:
            {
                byte[] p = new byte[4];
                WritePoint(p, 0, m.X, m.Y);
                return p;
            }
            case MouseButtonInput m:
            {
                byte[] p = new byte[6];
                p[0] = (byte)m.Button;
                p[1] = m.Down ? (byte)1 : (byte)0;
                WritePoint(p, 2, m.X, m.Y);
                return p;
            }
            case MouseWheelInput m:
            {
                byte[] p = new byte[8];
                BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(0, 2), m.DeltaX);
                BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(2, 2), m.DeltaY);
                WritePoint(p, 4, m.X, m.Y);
                return p;
            }
            case KeyInput m:
            {
                byte[] code = Encoding.ASCII.GetBytes(m.Code);
                if (code.Length > byte.MaxValue)
                    throw new ArgumentException("Key code longer than 255 bytes.", nameof(message));
                byte[] p = new byte[2 + code.Length];
                p[0] = m.Down ? (byte)1 : (byte)0;
                p[1] = (byte)code.Length;
                code.CopyTo(p, 2);
                return p;
            }
            case TextInput m:
                return Encoding.UTF8.GetBytes(m.Text);
            case ReleaseAllInput:
                return [];
            default:
                throw new ArgumentException($"Unsupported input message {message.GetType().Name}.", nameof(message));
        }
    }

    /// <summary>
    /// Parses an Input payload. Returns false (never throws) for an unknown type or a payload too short.
    /// Extra trailing bytes are ignored for forward compatibility.
    /// </summary>
    public static bool TryParse(byte type, ReadOnlySpan<byte> payload, out InputMessage? message)
    {
        message = null;
        switch ((InputType)type)
        {
            case InputType.MouseMove when payload.Length >= 4:
                message = new MouseMoveInput { X = ReadU16(payload, 0), Y = ReadU16(payload, 2) };
                return true;
            case InputType.MouseButton when payload.Length >= 6:
                message = new MouseButtonInput
                {
                    Button = (MouseButton)payload[0],
                    Down = payload[1] != 0,
                    X = ReadU16(payload, 2),
                    Y = ReadU16(payload, 4),
                };
                return true;
            case InputType.MouseWheel when payload.Length >= 8:
                message = new MouseWheelInput
                {
                    DeltaX = BinaryPrimitives.ReadInt16LittleEndian(payload[0..2]),
                    DeltaY = BinaryPrimitives.ReadInt16LittleEndian(payload[2..4]),
                    X = ReadU16(payload, 4),
                    Y = ReadU16(payload, 6),
                };
                return true;
            case InputType.Key when payload.Length >= 2 && payload.Length >= 2 + payload[1]:
                message = new KeyInput { Down = payload[0] != 0, Code = Encoding.ASCII.GetString(payload.Slice(2, payload[1])) };
                return true;
            case InputType.Text:
                message = new TextInput { Text = Encoding.UTF8.GetString(payload) };
                return true;
            case InputType.ReleaseAll:
                message = new ReleaseAllInput();
                return true;
            default:
                return false;
        }
    }

    private static void WritePoint(byte[] buffer, int offset, ushort x, ushort y)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), x);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 2, 2), y);
    }

    private static ushort ReadU16(ReadOnlySpan<byte> payload, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset, 2));
}
