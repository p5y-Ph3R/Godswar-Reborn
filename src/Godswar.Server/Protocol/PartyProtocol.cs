using System.Buffers.Binary;
using System.Text;

namespace Godswar.Server.Protocol;

internal static class PartyProtocol
{
    public const int MaximumMembers = 5;
    public const int ActionPacketBytes = 136;
    public const int ActionObjectIdOffset = 4;
    public const int FirstNameOffset = 8;
    public const int SecondNameOffset = 72;
    public const int NameBytes = 64;
    public const int RefreshPacketBytes = 484;
    public const int RefreshMemberBytes = 96;

    public static bool IsClientAction(ushort opcode) => opcode is
        Opcodes.PartyInvite or
        Opcodes.PartyAccept or
        Opcodes.PartyRemove or
        Opcodes.PartyChangeLeader or
        Opcodes.PartyDissolve or
        Opcodes.PartyLeave or
        Opcodes.PartyReject;

    public static bool TryReadAction(
        GamePacket packet,
        out PartyActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(packet);
        request = default;
        if (!IsClientAction(packet.Opcode) ||
            packet.Length != ActionPacketBytes ||
            packet.Buffer.Length != ActionPacketBytes)
        {
            return false;
        }

        request = new PartyActionRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.Buffer.AsSpan(ActionObjectIdOffset, sizeof(uint))),
            ReadName(packet.Buffer.AsSpan(FirstNameOffset, NameBytes)),
            ReadName(packet.Buffer.AsSpan(SecondNameOffset, NameBytes)));
        return true;
    }

    private static string ReadName(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end >= 0)
        {
            bytes = bytes[..end];
        }

        return Encoding.ASCII.GetString(bytes).Trim();
    }
}

internal readonly record struct PartyActionRequest(
    uint ClaimedObjectId,
    string FirstName,
    string SecondName);
