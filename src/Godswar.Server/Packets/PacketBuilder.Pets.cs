using System.Buffers.Binary;
using Godswar.Server.Application.Pets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const ushort OwnedPetListOpcode = 0x27FD;
    private const ushort PetOperationResultOpcode = 0x2804;
    private const ushort PetSkillStateOpcode = 0x2807;
    private const ushort PetShedExpansionResultOpcode = 0x2809;
    private const int OwnedPetListHeaderLength = 8;
    private const int OwnedPetRecordLength = 0xA8;
    private const int OwnedPetNameLength = 32;
    private const int OwnedPetMaximumSkillCount = 12;

    public static byte[] PackedPetDetail(
        IPetContentCatalog petContent,
        PetBootstrapSnapshot pet)
    {
        ArgumentNullException.ThrowIfNull(petContent);
        ArgumentNullException.ThrowIfNull(pet);
        var packet = new byte[sizeof(ushort) * 2 + OwnedPetRecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(sizeof(ushort)),
            Opcodes.PackedPetDetailResponse);
        WriteOwnedPetRecord(
            petContent,
            packet.AsSpan(sizeof(ushort) * 2, OwnedPetRecordLength),
            pet);
        return packet;
    }

    /// <summary>
    /// Builds the stock client's live pet skill-cell refresh (opcode 10247).
    /// This updates one existing pet bean without rebuilding carry/summon
    /// presentation state.
    /// </summary>
    public static byte[] PetSkillState(PetBootstrapSnapshot pet)
    {
        ArgumentNullException.ThrowIfNull(pet);
        if (pet.PetId is <= 0 or > uint.MaxValue)
        {
            throw new InvalidDataException(
                $"Pet ID {pet.PetId} cannot be represented by the native client.");
        }

        var activeSkills = pet.Skills
            .Where(static skill => skill.IsActive)
            .OrderBy(static skill => skill.SlotIndex)
            .ToArray();
        ValidatePetSkillCells(pet, activeSkills);

        var packet = new byte[36];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 36);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            PetSkillStateOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            checked((uint)pet.PetId));
        packet[8] = checked((byte)pet.AvailableSkillSlots);
        packet[9] = checked((byte)pet.OpenedSkillSlots);
        packet[10] = checked((byte)activeSkills.Length);
        WritePetSkillIds(
            packet.AsSpan(12, OwnedPetMaximumSkillCount * sizeof(ushort)),
            pet.PetId,
            activeSkills);
        return packet;
    }

    public static byte[] PetOperationResult(
        uint petId,
        PetOperationResultCode result)
    {
        if (!Enum.IsDefined(result))
        {
            throw new ArgumentOutOfRangeException(
                nameof(result),
                result,
                "Unknown native pet-operation result code.");
        }

        var packet = new byte[9];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 9);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            PetOperationResultOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            petId);
        packet[8] = (byte)result;
        return packet;
    }

    public static byte[] PetShedExpansionResult(
        PetShedExpansionResultCode result)
    {
        if (!Enum.IsDefined(result))
        {
            throw new ArgumentOutOfRangeException(
                nameof(result),
                result,
                "Unknown native pet-shed expansion result code.");
        }

        var packet = new byte[5];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 5);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            PetShedExpansionResultOpcode);
        packet[4] = (byte)result;
        return packet;
    }

    /// <summary>
    /// Builds the native client's complete owned-pet bootstrap (opcode 10237).
    /// The wire layout is backed by original-server captures and the native
    /// 0x006A6340 record-copy routine.
    /// </summary>
    public static byte[] OwnedPetList(
        IPetContentCatalog petContent,
        IReadOnlyList<PetBootstrapSnapshot> pets,
        int openedCellCount)
    {
        ArgumentNullException.ThrowIfNull(petContent);
        ArgumentNullException.ThrowIfNull(pets);
        if (pets.Count > petContent.Settings.MaximumOwnedPetCount)
        {
            throw new InvalidDataException(
                $"The native client supports at most {petContent.Settings.MaximumOwnedPetCount} owned pets.");
        }
        if (!PetShedCapacityPolicy.IsValid(openedCellCount) ||
            openedCellCount > petContent.Settings.MaximumOwnedPetCount ||
            pets.Count > openedCellCount)
        {
            throw new InvalidDataException(
                "The owned-pet projection exceeds its persisted opened shed cells.");
        }

        if (pets.Count(static pet => pet.IsCarried) > 1)
        {
            throw new InvalidDataException(
                "The native client supports at most one carried pet.");
        }

        if (pets.Any(static pet => pet.IsSummoned && !pet.IsCarried))
        {
            throw new InvalidDataException(
                "A summoned pet must also be the carried pet.");
        }

        var packet = new byte[
            OwnedPetListHeaderLength + (pets.Count * OwnedPetRecordLength)];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, sizeof(ushort)),
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)),
            OwnedPetListOpcode);
        packet[4] = checked((byte)openedCellCount);
        packet[5] = checked((byte)pets.Count);

        var petIds = new HashSet<long>();
        for (var index = 0; index < pets.Count; index++)
        {
            var pet = pets[index];
            if (!petIds.Add(pet.PetId))
            {
                throw new InvalidDataException(
                    $"Owned-pet bootstrap contains duplicate pet ID {pet.PetId}.");
            }

            WriteOwnedPetRecord(
                petContent,
                packet.AsSpan(
                    OwnedPetListHeaderLength + (index * OwnedPetRecordLength),
                    OwnedPetRecordLength),
                pet);
        }

        return packet;
    }

    private static void WriteOwnedPetRecord(
        IPetContentCatalog petContent,
        Span<byte> record,
        PetBootstrapSnapshot pet)
    {
        PetSavvyRuntimeSemantics.ValidateProjectionProvenance(pet);
        if (pet.PetId is <= 0 or > uint.MaxValue)
        {
            throw new InvalidDataException(
                $"Pet ID {pet.PetId} cannot be represented by the native client.");
        }

        if (!petContent.TryGetSpecies(pet.SpeciesId, out var species))
        {
            throw new InvalidDataException(
                $"Pet {pet.PetId} has unknown species {pet.SpeciesId}.");
        }

        var aptitude = checked((int)pet.Aptitude);
        if (!petContent.TryGetAptitude(checked((short)pet.Aptitude), out _) ||
            pet.Sex > 1 ||
            pet.Level < petContent.Settings.MinimumLevel ||
            pet.Level > petContent.Settings.MaximumLevel)
        {
            throw new InvalidDataException(
                $"Pet {pet.PetId} has a native-incompatible aptitude, sex, or level.");
        }

        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(
            record,
            checked((uint)pet.PetId));
        // The native handler scans this fixed field for a NUL without applying
        // its own 32-byte bound. Reserve the final byte as a terminator.
        PacketText.WriteFixedAscii(
            record.Slice(0x04, OwnedPetNameLength - 1),
            pet.Name);
        record[0x24] = checked((byte)pet.SpeciesId);
        record[0x25] = checked((byte)aptitude);
        record[0x26] = checked((byte)species.FoodKind);
        record[0x27] = pet.Sex;
        record[0x28] = checked((byte)pet.Level);

        var activeSkills = pet.Skills
            .Where(static skill => skill.IsActive)
            .OrderBy(static skill => skill.SlotIndex)
            .ToArray();
        if (activeSkills.Length > petContent.Settings.MaximumSkillCount)
        {
            throw new InvalidDataException(
                $"Pet {pet.PetId} exceeds the published " +
                $"{petContent.Settings.MaximumSkillCount}-skill limit.");
        }
        ValidatePetSkillCells(pet, activeSkills);
        if (pet.AvailableSkillSlots >
                petContent.Settings.MaximumSkillCount ||
            pet.TalentMask is < 0 or > 31 ||
            pet.HasOwnerMergeTalent != ((pet.TalentMask & 16) != 0))
        {
            throw new InvalidDataException(
                $"Pet {pet.PetId} has invalid skill-cell or talent state.");
        }
        WritePetSkills(record, pet.PetId, activeSkills);

        // Native detail cells distinguish learned, opened/unsealed, and
        // available boundaries. Pet Enhance Spring raises the available
        // boundary; Golden Apple Juice raises the opened boundary.
        record[0x2B] = checked((byte)pet.OpenedSkillSlots);
        record[0x2C] = checked((byte)pet.AvailableSkillSlots);
        record[0x2D] = ToPercentageByte(pet.Satiety);
        record[0x2E] = ToPercentageByte(pet.Amity);
        // Native record parsing copies 0x2F into the pet bean's independent
        // flag at +0x47. The owned-list handler reads the following byte,
        // copied to +0x48, to select the carried pet and arm the left-panel
        // summon/recall paw. Values 3 and 4 are reserved for dispatch/work.
        record[0x2F] = 1;
        record[0x30] = pet.IsCarried ? (byte)1 : (byte)0;
        record[0x31] = checked((byte)activeSkills.Length);

        var currentLifetime = ToUInt16(pet.RemainingLifetime);
        var maximumLifetime = ResolveMaximumPetLifetime(
            species,
            currentLifetime);
        BinaryPrimitives.WriteUInt16LittleEndian(
            record.Slice(0x4A, sizeof(ushort)),
            currentLifetime);
        BinaryPrimitives.WriteUInt16LittleEndian(
            record.Slice(0x4C, sizeof(ushort)),
            maximumLifetime);

        // These four unresolved dwords are consistently 1 in every populated
        // working-server record. Preserve that client-safe baseline.
        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(0x50, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(0x54, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(0x58, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(0x5C, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.Slice(0x60, 4),
            ToUInt32(pet.Experience));
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.Slice(0x64, 4),
            ResolveCapturedPetNextLevelExperience(petContent, pet.Level));

        // Stock labels and Message_Pet.dat establish bits 0..4: Random Event,
        // Quest Dispatch, Work, Healing, and Merge. The database-published
        // aptitude definition owns the mask. Bit 5 is reserved and stays clear.
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.Slice(0x68, sizeof(uint)),
            checked((uint)pet.TalentMask));
        WritePetSavvy(record.Slice(0x6C, 24), pet, added: false);
        WritePetSavvy(record.Slice(0x84, 24), pet, added: true);
        BinaryPrimitives.WriteUInt16LittleEndian(
            record.Slice(0x9C, sizeof(ushort)),
            PetRankWirePolicy.Encode(pet.Rank));

        var totalRebirthAllowance = checked(
            (int)pet.CompletedRebirths + pet.RebirthsRemaining);
        record[0x9F] = ToByte(totalRebirthAllowance);
        record[0xA0] = ToByte(pet.CompletedRebirths);
        var soulContractStage = pet.ProjectedSoulContractStage;
        if (soulContractStage > PetSoulContractPolicy.MaximumStage)
        {
            throw new InvalidDataException(
                $"Pet {pet.PetId} has invalid Soul Contract stage.");
        }
        record[0xA1] = soulContractStage;
        BinaryPrimitives.WriteUInt16LittleEndian(
            record.Slice(0xA2, sizeof(ushort)),
            ToUInt16(pet.CompletedPetMerges));
        record[0xA4] = pet.IsBound ? (byte)1 : (byte)0;
    }

    private static void WritePetSkills(
        Span<byte> record,
        long petId,
        IReadOnlyList<PetSkillSnapshot> skills)
    {
        WritePetSkillIds(
            record.Slice(0x32, OwnedPetMaximumSkillCount * sizeof(ushort)),
            petId,
            skills);
    }

    private static void WritePetSkillIds(
        Span<byte> destination,
        long petId,
        IReadOnlyList<PetSkillSnapshot> skills)
    {
        if (destination.Length !=
                OwnedPetMaximumSkillCount * sizeof(ushort) ||
            skills.Count > OwnedPetMaximumSkillCount)
        {
            throw new InvalidDataException(
                $"Pet {petId} exceeds the native {OwnedPetMaximumSkillCount}-skill limit.");
        }

        for (var index = 0; index < skills.Count; index++)
        {
            var skill = skills[index];
            if (skill.SlotIndex != index ||
                skill.SkillId is <= 0 or > ushort.MaxValue)
            {
                throw new InvalidDataException(
                    $"Pet {petId} must have contiguous active native skill slots.");
            }

            BinaryPrimitives.WriteUInt16LittleEndian(
                destination.Slice(skill.SlotIndex * sizeof(ushort), 2),
                checked((ushort)skill.SkillId));
        }
    }

    private static void ValidatePetSkillCells(
        PetBootstrapSnapshot pet,
        IReadOnlyList<PetSkillSnapshot> activeSkills)
    {
        if (pet.OpenedSkillSlots is < 1 or > OwnedPetMaximumSkillCount ||
            pet.AvailableSkillSlots is < 1 or > OwnedPetMaximumSkillCount ||
            pet.OpenedSkillSlots > pet.AvailableSkillSlots ||
            activeSkills.Count > pet.OpenedSkillSlots ||
            activeSkills.Any(skill =>
                skill.SlotIndex >= pet.OpenedSkillSlots))
        {
            throw new InvalidDataException(
                $"Pet {pet.PetId} has invalid skill-cell state.");
        }
    }

    private static void WritePetSavvy(
        Span<byte> destination,
        PetBootstrapSnapshot pet,
        bool added)
    {
        var values = pet.StatValues
            .GroupBy(static value => value.StatCode)
            .ToDictionary(static group => group.Key, static group => group.Single());
        for (short statCode = 1; statCode <= 6; statCode++)
        {
            var value = values.TryGetValue(statCode, out var stat)
                ? ResolvePetSavvyWireValue(pet.Level, stat, added)
                : 0m;
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination.Slice((statCode - 1) * sizeof(uint), sizeof(uint)),
                ToFixedPointUInt32(value));
        }
    }

    private static decimal ResolvePetSavvyWireValue(
        int petLevel,
        PetStatValueSnapshot stat,
        bool added)
    {
        return added
            ? PetSavvyRuntimeSemantics.ResolveNativeAdded(
                petLevel,
                stat.AddedSavvy,
                stat.BaseGrowthRate,
                stat.GrowthAcceleration,
                stat.RarityAddedSavvy)
            : PetSavvyRuntimeSemantics.ResolveNativeBasic(
                stat.InitialSavvy,
                stat.RarityAddedSavvy);
    }

    private static ushort ResolveMaximumPetLifetime(
        PetSpeciesContentDefinition species,
        ushort currentLifetime)
    {
        var candidate = species.LifetimeValues
            .Where(value => value >= currentLifetime)
            .Order()
            .FirstOrDefault();
        return candidate > 0
            ? ToUInt16(candidate)
            : currentLifetime;
    }

    private static uint ResolveCapturedPetNextLevelExperience(
        IPetContentCatalog petContent,
        short level) =>
        checked((uint)petContent.RequiredExperienceForNextLevel(level));

    private static byte ToPercentageByte(int value) =>
        checked((byte)Math.Clamp(value, 0, 100));

    private static byte ToByte(int value) =>
        checked((byte)Math.Clamp(value, byte.MinValue, byte.MaxValue));

    private static ushort ToUInt16(int value) =>
        checked((ushort)Math.Clamp(value, ushort.MinValue, ushort.MaxValue));

    private static uint ToUInt32(long value) =>
        checked((uint)Math.Clamp(value, uint.MinValue, uint.MaxValue));

    private static uint ToFixedPointUInt32(decimal value) =>
        checked((uint)Math.Clamp(
            decimal.Round(value * 100m, 0, MidpointRounding.AwayFromZero),
            uint.MinValue,
            uint.MaxValue));
}

internal enum PetOperationResultCode : byte
{
    TakeSucceeded = 1,
    TakeFailed = 2,
    RecallSucceeded = 5,
    RecallFailed = 6,
    CallOutSucceeded = 7,
    CallOutFailed = 8
}

internal enum PetShedExpansionResultCode : byte
{
    AlreadyMaximum = 2,
    Succeeded = 11
}
