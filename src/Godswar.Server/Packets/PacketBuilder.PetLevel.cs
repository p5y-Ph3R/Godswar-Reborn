using System.Buffers.Binary;
using Godswar.Server.Application.Pets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int PetLevelUpgradePacketLength = 68;
    private const int PetAppearanceRefreshPacketLength = 72;
    private const int PetGenderRefreshPacketLength = 76;

    public static byte[] PetLevelUpgrade(
        IPetContentCatalog petContent,
        uint petId,
        int level,
        long currentExperience,
        PetSavvy basicSavvy,
        PetSavvy addedSavvy)
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
        if (!addedSavvy.IsNonNegative)
        {
            throw new ArgumentOutOfRangeException(
                nameof(addedSavvy),
                addedSavvy,
                "Pet Added Savvy values cannot be negative.");
        }

        return BuildPetProgressionRefresh(
            petContent,
            petId,
            level,
            currentExperience,
            basicSavvy,
            addedSavvy,
            PetLevelUpgradePacketLength);
    }

    /// <summary>
    /// Builds the narrow patched-client refresh used after appearance or bind
    /// mutations. It preserves the established 68-byte progression prefix and
    /// appends authoritative one-byte species and bound fields; it never
    /// rebuilds the owned-pet collection.
    /// </summary>
    public static byte[] PetAppearanceRefresh(
        IPetContentCatalog petContent,
        PetBootstrapSnapshot pet)
    {
        ArgumentNullException.ThrowIfNull(pet);
        var ordered = pet.StatValues
            .OrderBy(static stat => stat.StatCode)
            .ToArray();
        if (ordered.Length != 6 ||
            ordered.Where((stat, index) =>
                stat.StatCode != index + 1).Any())
        {
            throw new InvalidDataException(
                "A pet appearance refresh requires six ordered stats.");
        }
        if (pet.SpeciesId is < 1 or > 45)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pet),
                pet.SpeciesId,
                "Pet species is outside the patched client's range.");
        }

        var basic = ToPetSavvy(ordered.Select(static stat =>
            PetSavvyRuntimeSemantics.ResolveNativeBasic(
                stat.InitialSavvy,
                stat.RarityAddedSavvy)));
        PetSavvyRuntimeSemantics.ValidateProjectionProvenance(pet);
        var added = ToPetSavvy(ordered.Select(stat =>
            PetSavvyRuntimeSemantics.ResolveNativeAdded(
                pet.Level,
                stat.AddedSavvy,
                stat.BaseGrowthRate,
                stat.GrowthAcceleration,
                stat.RarityAddedSavvy)));
        var packet = BuildPetProgressionRefresh(
            petContent,
            checked((uint)pet.PetId),
            pet.Level,
            pet.Experience,
            basic,
            added,
            PetAppearanceRefreshPacketLength);
        packet[68] = checked((byte)pet.SpeciesId);
        packet[69] = pet.IsBound ? (byte)1 : (byte)0;
        return packet;
    }

    /// <summary>
    /// Extends the exact narrow appearance refresh with one authoritative sex
    /// byte. The guarded client accepts this 76-byte successor independently
    /// of the established 68- and 72-byte forms.
    /// </summary>
    public static byte[] PetGenderRefresh(
        IPetContentCatalog petContent,
        PetBootstrapSnapshot pet)
    {
        ArgumentNullException.ThrowIfNull(pet);
        if (pet.Sex > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pet),
                pet.Sex,
                "Pet sex is outside the native client's range.");
        }
        var packet = PetAppearanceRefresh(petContent, pet);
        Array.Resize(ref packet, PetGenderRefreshPacketLength);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            PetGenderRefreshPacketLength);
        packet[72] = pet.Sex;
        return packet;
    }

    private static byte[] BuildPetProgressionRefresh(
        IPetContentCatalog petContent,
        uint petId,
        int level,
        long currentExperience,
        PetSavvy basicSavvy,
        PetSavvy addedSavvy,
        int packetLength)
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
        if (!addedSavvy.IsNonNegative)
        {
            throw new ArgumentOutOfRangeException(
                nameof(addedSavvy),
                addedSavvy,
                "Pet Added Savvy values cannot be negative.");
        }

        var packet = new byte[packetLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packetLength));
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
        WritePetSavvyValues(
            packet.AsSpan(20),
            basicSavvy);
        WritePetSavvyValues(
            packet.AsSpan(44),
            addedSavvy);
        return packet;
    }

    private static PetSavvy ToPetSavvy(IEnumerable<decimal> values)
    {
        var ordered = values.ToArray();
        if (ordered.Length != 6)
        {
            throw new InvalidDataException(
                "A native pet Savvy projection requires six values.");
        }
        return new PetSavvy(
            ordered[0],
            ordered[1],
            ordered[2],
            ordered[3],
            ordered[4],
            ordered[5]);
    }

    private static void WritePetSavvyValues(
        Span<byte> destination,
        PetSavvy values)
    {
        decimal[] orderedValues =
        [
            values.Agility,
            values.Strength,
            values.Accuracy,
            values.Technique,
            values.Wisdom,
            values.Luck
        ];
        for (var index = 0; index < orderedValues.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination.Slice(index * sizeof(uint), sizeof(uint)),
                ToFixedPointUInt32(orderedValues[index]));
        }
    }
}
