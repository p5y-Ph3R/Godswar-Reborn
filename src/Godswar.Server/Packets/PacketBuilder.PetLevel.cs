using System.Buffers.Binary;
using Godswar.Server.Application.Pets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int PetLevelUpgradePacketLength = 44;

    public static byte[] PetLevelUpgrade(
        IPetContentCatalog petContent,
        uint petId,
        int level,
        long currentExperience,
        PetSavvy basicSavvy)
    {
        ArgumentNullException.ThrowIfNull(petContent);
        if (petId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(petId),
                petId,
                "Pet ID zero is not valid.");
        }

        if (level < petContent.Settings.MinimumLevel ||
            level > petContent.Settings.MaximumLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "Pet level is outside the native client's range.");
        }

        if (currentExperience < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentExperience),
                currentExperience,
                "Pet experience cannot be negative.");
        }
        if (!basicSavvy.IsNonNegative)
        {
            throw new ArgumentOutOfRangeException(
                nameof(basicSavvy),
                basicSavvy,
                "Pet basic-savvy values cannot be negative.");
        }

        var packet = new byte[PetLevelUpgradePacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            PetLevelUpgradePacketLength);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PetLevelUpgrade);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            petId);
        packet[8] = checked((byte)level);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12),
            ToUInt32(currentExperience));
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(16),
            checked((uint)petContent.RequiredExperienceForNextLevel(level)));
        WritePetBasicSavvy(
            packet.AsSpan(20),
            basicSavvy);
        return packet;
    }

    private static void WritePetBasicSavvy(
        Span<byte> destination,
        PetSavvy basicSavvy)
    {
        decimal[] values =
        [
            basicSavvy.Agility,
            basicSavvy.Strength,
            basicSavvy.Accuracy,
            basicSavvy.Technique,
            basicSavvy.Wisdom,
            basicSavvy.Luck
        ];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination.Slice(index * sizeof(uint), sizeof(uint)),
                ToFixedPointUInt32(values[index]));
        }
    }
}
