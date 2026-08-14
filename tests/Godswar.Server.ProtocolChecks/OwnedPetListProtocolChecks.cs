using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class OwnedPetListProtocolChecks
{
    private const ushort OwnedPetListOpcode = 10_237;
    private const int PetRecordLength = 0xA8;
    private const int AccountId = 13;
    private const int CharacterId = 2;

    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    public static async Task RunAsync()
    {
        CheckCanonicalEmptyPacket();
        CheckGodlyKingLionRecord();
        CheckRankWireSafety();
        CheckScaledAddedV3Projection();
        CheckCapacityThresholdsAndValidation();
        await CheckLoginBootstrapOrderingAsync();
    }

    private static void CheckScaledAddedV3Projection()
    {
        var pet = CreateGodlyKingLion() with
        {
            InitialSavvySourceVersion =
                PetSavvyRuntimeSemantics.SourceVersion,
            StatValues =
            [
                new PetStatValueSnapshot(
                    1,
                    InitialSavvy: 2_658.653337m,
                    AddedSavvy: 16.767423m * 80m,
                    BaseGrowthRate: 16.767423m,
                    GrowthAcceleration: 0m,
                    Revision: 209,
                    BirthInitialSavvy: 663.33m,
                    RarityAddedSavvy: 663.33m),
                ScaledStat(2, 2m, 12m, 80),
                ScaledStat(3, 3m, 13m, 80),
                ScaledStat(4, 4m, 14m, 80),
                ScaledStat(5, 5m, 15m, 80),
                ScaledStat(6, 6m, 16m, 80)
            ]
        };

        var packet = PacketBuilder.OwnedPetList(
            PetContentTestCatalog.Instance,
            [pet],
            openedCellCount: 2);
        var record = packet.AsSpan(8, PetRecordLength);

        Check.Equal(
            ToFixedPoint(2_658.653337m),
            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(0x6C, 4)),
            "scaled-Added v3 Basic remains the persisted Merge value");
        Check.Equal(
            ToFixedPoint(16.767423m * pet.Level),
            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(0x84, 4)),
            "scaled-Added v3 projects Growth Rate multiplied by pet level");
        Check.Equal(
            ToFixedPoint(2m),
            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(0x70, 4)),
            "scaled-Added v3 preserves the second Basic value");
        Check.Equal(
            ToFixedPoint(12m * pet.Level),
            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(0x88, 4)),
            "scaled-Added v3 scales every Added value by pet level");

        var stale = pet with
        {
            StatValues = pet.StatValues
                .Select(stat => stat.StatCode == 1
                    ? stat with { AddedSavvy = stat.BaseGrowthRate }
                    : stat)
                .ToArray()
        };
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(
                PetContentTestCatalog.Instance,
                [stale],
                openedCellCount: 2),
            "10237 rejects stale scaled-Added materialization");
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(
                PetContentTestCatalog.Instance,
                [pet with
                {
                    InitialSavvySourceVersion = "savvy-plus-growth-v2"
                }],
                openedCellCount: 2),
            "10237 rejects obsolete Savvy provenance");
    }

    private static PetStatValueSnapshot ScaledStat(
        short code,
        decimal basic,
        decimal growth,
        int level) =>
        new(
            code,
            basic,
            growth * level,
            growth,
            0m,
            Revision: 1,
            BirthInitialSavvy: basic,
            RarityAddedSavvy: basic);

    private static void CheckGodlyKingLionRecord()
    {
        var pet = CreateGodlyKingLion();
        var packet = PacketBuilder.OwnedPetList(
            PetContentTestCatalog.Instance,
            [pet],
            openedCellCount: 2);
        var record = packet.AsSpan(8, PetRecordLength);

        Check.Equal(176, packet.Length, "one-pet packet length");
        Check.Equal(
            (ushort)packet.Length,
            ReadUInt16(packet, 0),
            "one-pet declared length");
        Check.Equal(
            OwnedPetListOpcode,
            ReadUInt16(packet, 2),
            "owned-pet list opcode");
        Check.Equal((byte)2, packet[4], "one-pet cell capacity");
        Check.Equal((byte)1, packet[5], "one-pet record count");
        Check.Equal((ushort)0, ReadUInt16(packet, 6), "header reserved bytes");

        Check.Equal(
            checked((uint)pet.PetId),
            BinaryPrimitives.ReadUInt32LittleEndian(record),
            "pet ID");
        Check.Equal(
            "Godly King Lion",
            ReadFixedAscii(record.Slice(0x04, 32)),
            "pet name");
        var longNamePacket = PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance,
            [pet with { Name = new string('X', 32) }],
            openedCellCount: 2);
        var longNameField = longNamePacket.AsSpan(12, 32);
        Check.True(
            longNameField[..31].IndexOfAnyExcept((byte)'X') < 0,
            "pet name retains at most 31 ASCII bytes");
        Check.Equal(
            (byte)0,
            longNameField[31],
            "pet name always reserves a native NUL terminator");
        Check.Equal((byte)37, record[0x24], "King Lion species");
        Check.Equal(
            checked((byte)PetAptitude.Godly),
            record[0x25],
            "Godly aptitude");
        Check.Equal(
            checked((byte)PetFoodKind.Carnivore),
            record[0x26],
            "King Lion food kind");
        Check.Equal((byte)1, record[0x27], "pet sex");
        Check.Equal((byte)80, record[0x28], "pet level");
        Check.Equal((byte)88, record[0x2D], "pet satiety");
        Check.Equal((byte)77, record[0x2E], "pet amity");
        Check.Equal(
            (byte)1,
            record[0x2F],
            "captured per-record flag remains independent of carried state");
        Check.Equal(
            (byte)0,
            record[0x30],
            "uncarried pet leaves the native active-pet selector clear");

        var carriedPacket = PacketBuilder.OwnedPetList(
            PetContentTestCatalog.Instance,
            [pet with { IsCarried = true }],
            openedCellCount: 2);
        Check.Equal(
            (byte)1,
            carriedPacket[8 + 0x30],
            "carried pet arms the native left-panel summon/recall control");

        Check.Equal(
            (byte)2,
            record[0x2B],
            "opened skill-cell boundary");
        Check.Equal(
            (byte)3,
            record[0x2C],
            "available skill-cell boundary");
        Check.Equal((byte)2, record[0x31], "learned skill count");
        Check.Equal(
            (ushort)5_200,
            BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x32, 2)),
            "starter skill in slot zero");
        Check.Equal(
            (ushort)5_555,
            BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x34, 2)),
            "second active skill retains slot one");
        Check.Equal(
            (ushort)0,
            BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x40, 2)),
            "inactive skill is not serialized");

        Check.Equal(
            (ushort)1_100,
            BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x4A, 2)),
            "remaining lifetime");
        Check.Equal(
            (ushort)1_200,
            BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x4C, 2)),
            "King Lion maximum lifetime");
        Check.Equal(
            1u,
            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(0x5C, 4)),
            "captured unresolved dword at record offset 0x5C");
        Check.Equal(
            123_456u,
            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(0x60, 4)),
            "pet experience");
        Check.Equal(
            31u,
            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(0x68, 4)),
            "five captured pet-talent bits");

        var expectedInitialSavvy =
            new decimal[] { 1.25m, 2.5m, 3.75m, 4m, 5.5m, 6.25m };
        var expectedAddedSavvy =
            new decimal[] { 7.5m, 8.25m, 9m, 10.75m, 11.5m, 12.25m };
        for (var index = 0; index < 6; index++)
        {
            Check.Equal(
                ToFixedPoint(expectedInitialSavvy[index]),
                BinaryPrimitives.ReadUInt32LittleEndian(
                    record.Slice(0x6C + (index * 4), 4)),
                $"initial savvy {index + 1}");
            Check.Equal(
                ToFixedPoint(expectedAddedSavvy[index]),
                BinaryPrimitives.ReadUInt32LittleEndian(
                    record.Slice(0x84 + (index * 4), 4)),
                $"added savvy {index + 1}");
        }

        Check.Equal(
            (ushort)2_525,
            BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x9C, 2)),
            "pet rank fixed-point value");
        Check.Equal(
            (byte)5,
            record[0x9F],
            "total rebirth allowance");
        Check.Equal(
            (byte)3,
            record[0xA0],
            "completed rebirth count");
        Check.Equal((byte)6, record[0xA1], "soul-contract stage");
        Check.Equal(
            (ushort)7,
            BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0xA2, 2)),
            "completed pet-merge count");
        Check.Equal((byte)1, record[0xA4], "pet bind flag");
    }

    private static void CheckCapacityThresholdsAndValidation()
    {
        CheckCapacity(count: 0, openedCellCount: 2);
        CheckCapacity(count: 1, openedCellCount: 2);
        CheckCapacity(count: 2, openedCellCount: 2);
        CheckCapacity(count: 2, openedCellCount: 3);
        CheckCapacity(count: 4, openedCellCount: 4);
        CheckCapacity(count: 7, openedCellCount: 8);
        CheckCapacity(count: 8, openedCellCount: 8);

        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(
                PetContentTestCatalog.Instance,
                CreatePets(3),
                openedCellCount: 2),
            "owned pets cannot exceed independently opened shed cells");

        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(
                PetContentTestCatalog.Instance,
                CreatePets(9),
                openedCellCount: 8),
            "native eight-pet limit is enforced");

        var duplicate = CreateGodlyKingLion();
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance,
                [duplicate, duplicate with { Name = "Duplicate" }],
                openedCellCount: 2),
            "duplicate pet IDs are rejected");

        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance,
                [duplicate with { SpeciesId = short.MaxValue }],
                openedCellCount: 2),
            "unknown species are rejected");
        var publishedSkillOverflow = duplicate with
        {
            Skills = Enumerable.Range(
                    0,
                    PetContentTestCatalog.Instance.Settings
                        .MaximumSkillCount + 1)
                .Select(static index => new PetSkillSnapshot(
                    5_200 + index,
                    checked((short)index),
                    1,
                    0,
                    true,
                    index + 1))
                .ToArray()
        };
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(
                PetContentTestCatalog.Instance,
                [publishedSkillOverflow],
                openedCellCount: 2),
            "published pet skill limit governs the runtime wire projection");
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance,
                [duplicate with { Sex = 2 }],
                openedCellCount: 2),
            "native-incompatible pet sex is rejected");
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance,
                [
                    duplicate with
                    {
                        Aptitude = (PetAptitude)17
                    }
                ],
                openedCellCount: 2),
            "undefined aptitude tiers are rejected");
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance,
                [
                    duplicate with
                    {
                        IsSummoned = true,
                        IsCarried = false
                    }
                ],
                openedCellCount: 2),
            "summoned pet must be carried");
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance,
                [
                    duplicate with { IsCarried = true },
                    duplicate with
                    {
                        PetId = 2,
                        Name = "Second",
                        IsCarried = true
                    }
                ],
                openedCellCount: 2),
            "only one pet can be carried");

        var duplicateSkillSlots = duplicate with
        {
            Skills =
            [
                new PetSkillSnapshot(5_200, 0, 1, 0, true, 1),
                new PetSkillSnapshot(5_555, 0, 1, 0, true, 1)
            ]
        };
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(
                PetContentTestCatalog.Instance,
                [duplicateSkillSlots],
                openedCellCount: 2),
            "duplicate active pet-skill slots are rejected");

        var sparseSkillSlots = duplicate with
        {
            Skills =
            [
                new PetSkillSnapshot(5_200, 0, 1, 0, true, 1),
                new PetSkillSnapshot(5_555, 5, 1, 0, true, 1)
            ]
        };
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(
                PetContentTestCatalog.Instance,
                [sparseSkillSlots],
                openedCellCount: 2),
            "learned pet skills must occupy contiguous native slots");
    }

    private static async Task CheckLoginBootstrapOrderingAsync()
    {
        var character = CreateCharacter();
        var pet = CreateGodlyKingLion();
        var store = new LoginBootstrapStore(character, [pet]);
        var transport = new ScriptedLegacyByteTransport();
        await using var session = new ClientSession(transport);
        var handler = new GameClientHandler(
            session,
            store,
            new GameSessionRegistry(
                store: null,
                zodiacEnergyOptions: null,
                monsterRuntimeMode: MonsterRuntimeMode.Ecs,
                playerRuntimeMode: PlayerRuntimeMode.Ecs),
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty,
            petContent: PetContentTestCatalog.Instance);
        SetField(
            handler,
            "_account",
            new AccountIdentity(AccountId, "test2"));
        SetField(handler, "_character", character);
        SetField(
            handler,
            "_characterLoadSnapshot",
            new HydratedCharacterLoadSnapshot(
                character,
                [],
                [],
                new CharacterPetShedSnapshot(2, 0),
                [pet],
                []));
        SetField(handler, "_characterSnapshotLoaded", true);
        SetField(
            handler,
            "_characterSnapshotBootstrapPending",
            true);

        await InvokePacketAsync(handler, CreateOpcodePacket(Opcodes.EnterGame));

        var clearBytes = transport.WrittenBytes;
        new PacketCipher().Transform(clearBytes);
        var packets = SplitPackets(clearBytes);
        var petPacketIndex = packets.FindIndex(
            static packet => ReadUInt16(packet, 2) == OwnedPetListOpcode);
        Check.True(
            petPacketIndex >= 0,
            "enter flow sends the owned-pet list");
        var uiBootstrapIndex = packets.FindIndex(
            static packet => ReadUInt16(packet, 2) == 10_329);
        Check.True(
            uiBootstrapIndex >= 0,
            "enter flow sends the captured UI bootstrap");

        var expectedBagPackets = PacketBuilder
            .KitBagDetailPages(character)
            .Concat(PacketBuilder.KitBagSlotIndexes(character))
            .ToArray();
        Check.True(
            petPacketIndex >= expectedBagPackets.Length,
            "owned-pet list follows the complete bag bootstrap");
        var bagStart = petPacketIndex - expectedBagPackets.Length;
        Check.True(
            uiBootstrapIndex < bagStart,
            "deterministic server order preserves UI before bag bootstrap");
        for (var index = 0; index < expectedBagPackets.Length; index++)
        {
            Check.True(
                packets[bagStart + index].SequenceEqual(
                    expectedBagPackets[index]),
                $"bag packet {index} precedes owned-pet list unchanged");
        }

        Check.True(
            uiBootstrapIndex < petPacketIndex,
            "captured UI bootstrap precedes OwnedPetList");
        Check.True(
            packets[petPacketIndex].SequenceEqual(
                PacketBuilder.OwnedPetList(
                    PetContentTestCatalog.Instance,
                    [pet],
                    openedCellCount: 2)),
            "enter flow sends the persisted pet snapshot");
        Check.Equal(
            (ushort)10_196,
            ReadUInt16(packets[petPacketIndex + 1], 2),
            "SkillList immediately follows OwnedPetList");
        Check.Equal(
            Opcodes.GameServerReady,
            ReadUInt16(packets[petPacketIndex + 2], 2),
            "EnterComplete immediately follows SkillList");
        Check.Equal(
            packets.Count - 3,
            petPacketIndex,
            "no packet is inserted after the terminal enter sequence");
        Check.Equal(
            0,
            store.OwnedPetReadCount,
            "initial enter consumes pets from the single character snapshot");
    }

}
