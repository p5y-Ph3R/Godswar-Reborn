using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] LocalizedError(int errorCode)
    {
        if (errorCode <= 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(errorCode),
                "Localized error codes must not overlap repetition states.");
        }

        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionNotice);
        // The stock 10216 receiver treats values 0..3 as repetition states.
        // Larger values at +8 are formatted as ERROR_%0.4x and appended to
        // the native left-side message log. The +4 field is ignored there.
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            errorCode);
        return packet;
    }

    public static byte[] RepetitionNotice(
        int repetitionId,
        int invitationId)
    {
        if (repetitionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repetitionId),
                "Repetition notices require positive identities.");
        }
        if (invitationId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(invitationId),
                "Repetition notices require positive identities.");
        }

        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionNotice);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            repetitionId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            invitationId);
        return packet;
    }

    public static byte[] RepetitionInvitation(
        int repetitionId,
        int invitationId,
        string inviterName)
    {
        if (repetitionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repetitionId),
                "Repetition invitations require positive identities.");
        }
        if (invitationId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(invitationId),
                "Repetition invitations require positive identities.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(inviterName);

        const int headerAndIdsBytes = 12;
        var packet = new byte[
            headerAndIdsBytes + CharacterSnapshotLimits.CharacterNameLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionInvitation);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            repetitionId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            invitationId);
        PacketText.WriteFixedAscii(
            packet.AsSpan(
                headerAndIdsBytes,
                CharacterSnapshotLimits.CharacterNameLength),
            inviterName);
        return packet;
    }

    public static byte[] RepetitionCountdown(int seconds)
    {
        if (seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionReset);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            seconds);
        return packet;
    }

    public static byte[] RepetitionReset() => RepetitionCountdown(0);

    public static byte[] RepetitionPanelCompletion()
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 6);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionPanelAction);
        packet[4] = 3;
        return packet;
    }

    public static byte[] RepetitionCompletionState(
        int repetitionId,
        bool completed)
    {
        if (repetitionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(repetitionId));
        }

        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionCompletionState);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            repetitionId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            completed ? 1 : 0);
        return packet;
    }

    public static byte[] RepetitionInstanceMembers(
        IReadOnlyList<RepetitionInstanceMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        const int memberBytes = 44;
        var packet = new byte[checked(8 + members.Count * memberBytes)];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionInstanceMembers);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            members.Count);

        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index];
            if (member.CharacterId <= 0 || member.Level <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(members));
            }

            var offset = 8 + index * memberBytes;
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(offset),
                member.CharacterId);
            PacketText.WriteFixedAscii(
                packet.AsSpan(
                    offset + sizeof(int),
                    CharacterSnapshotLimits.CharacterNameLength),
                member.Name);
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(offset + 36),
                member.Level);
            packet[offset + 40] = member.IsOnline ? (byte)1 : (byte)0;
            packet[offset + 41] = member.Profession;
        }

        return packet;
    }

    public static byte[] RepetitionSync(
        ushort repetitionId,
        ushort repetitionIndex,
        ushort groupIndex,
        ushort state,
        ushort entryLimit)
    {
        if (repetitionId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repetitionId),
                "Repetition synchronization requires positive identities.");
        }

        var packet = new byte[14];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionSync);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(4),
            repetitionId);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(6),
            repetitionIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(8),
            groupIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(10),
            state);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(12),
            entryLimit);
        return packet;
    }

    public static byte[] RepetitionFightInfo(
        int remainingSeconds,
        int teamScore)
    {
        if (remainingSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(remainingSeconds));
        }
        if (teamScore < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(teamScore));
        }

        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionFightInfo);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            remainingSeconds);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            1);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12),
            1);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(16),
            teamScore);
        return packet;
    }

    public static byte[] RepetitionReward(int hardPoints)
    {
        if (hardPoints <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hardPoints));
        }

        // The external Medusa completion capture carries its documented
        // HardPoint award in the native SimplePoint/Honor field at +92. The
        // client exposes that shared balance as Honor in Character Details.
        var packet = new byte[104];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionReward);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            17);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(92),
            hardPoints);
        return packet;
    }
}

internal readonly record struct RepetitionInstanceMember(
    int CharacterId,
    string Name,
    int Level,
    bool IsOnline,
    byte Profession);
