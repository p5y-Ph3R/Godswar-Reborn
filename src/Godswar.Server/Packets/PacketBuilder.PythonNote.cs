using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int PythonNotePacketLength = 137;
    private const int PythonNoteFieldLength = 64;
    private const int PythonNoteTextPartLength = PythonNoteFieldLength - 1;
    internal const int CenteredAnnouncementMaximumTextLength =
        PythonNoteTextPartLength * 2;
    private const int PythonNoteDirectTextType = 50;
    private const byte PythonNoteCenterChannel = 0;

    /// <summary>
    /// Uses the stock client's direct-text announcement formatter. Its two
    /// fixed strings are concatenated before the center-screen proclamation
    /// is rendered, so splitting here does not alter the visible text.
    /// </summary>
    public static byte[] CenteredAnnouncement(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Any(static character => character > sbyte.MaxValue) ||
            message.Length > CenteredAnnouncementMaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                "Native centered announcements require at most 126 ASCII bytes.");
        }

        var packet = new byte[PythonNotePacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)PythonNotePacketLength));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PythonNote);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            PythonNoteDirectTextType);
        packet[8] = PythonNoteCenterChannel;

        var split = Math.Min(message.Length, PythonNoteTextPartLength);
        PacketText.WriteFixedAscii(
            packet.AsSpan(9, PythonNoteFieldLength),
            message[..split]);
        PacketText.WriteFixedAscii(
            packet.AsSpan(73, PythonNoteFieldLength),
            message[split..]);
        return packet;
    }
}
