using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Godswar.Server.Application.Accounts;
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
        CheckCapacityThresholdsAndValidation();
        await CheckLoginBootstrapOrderingAsync();
    }

    private static void CheckGodlyKingLionRecord()
    {
        var pet = CreateGodlyKingLion();
        var packet = PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance, [pet]);
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
            [pet with { Name = new string('X', 32) }]);
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
            (byte)2,
            record[0x2B],
            "opened skill-cell boundary");
        Check.Equal(
            (byte)2,
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
        Check.Equal((byte)1, record[0xA1], "soul-contract flag");
        Check.Equal(
            (ushort)7,
            BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0xA2, 2)),
            "completed pet-merge count");
        Check.Equal((byte)1, record[0xA4], "pet bind flag");
    }

    private static void CheckCapacityThresholdsAndValidation()
    {
        CheckCapacity(count: 0, expectedCapacity: 2);
        CheckCapacity(count: 2, expectedCapacity: 2);
        CheckCapacity(count: 3, expectedCapacity: 4);
        CheckCapacity(count: 4, expectedCapacity: 4);
        CheckCapacity(count: 5, expectedCapacity: 8);
        CheckCapacity(count: 8, expectedCapacity: 8);

        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance, CreatePets(9)),
            "native eight-pet limit is enforced");

        var duplicate = CreateGodlyKingLion();
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance,
                [duplicate, duplicate with { Name = "Duplicate" }]),
            "duplicate pet IDs are rejected");

        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance,
                [duplicate with { SpeciesId = short.MaxValue }]),
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
                [publishedSkillOverflow]),
            "published pet skill limit governs the runtime wire projection");
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance,
                [duplicate with { Sex = 2 }]),
            "native-incompatible pet sex is rejected");
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance,
                [
                    duplicate with
                    {
                        Aptitude = (PetAptitude)17
                    }
                ]),
            "undefined aptitude tiers are rejected");
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance,
                [
                    duplicate with
                    {
                        IsSummoned = true,
                        IsCarried = false
                    }
                ]),
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
                ]),
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
            () => PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance, [duplicateSkillSlots]),
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
            () => PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance, [sparseSkillSlots]),
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
                PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance, [pet])),
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

    private static PetBootstrapSnapshot[] CreatePets(int count) =>
        Enumerable.Range(1, count)
            .Select(index => CreateGodlyKingLion() with
            {
                PetId = index,
                Name = $"Lion {index}"
            })
            .ToArray();

    private static PetBootstrapSnapshot CreateGodlyKingLion() =>
        new(
            PetId: 1,
            AccountId: AccountId,
            OwnerCharacterId: CharacterId,
            SpeciesId: 37,
            Name: "Godly King Lion",
            Sex: 1,
            Level: 80,
            Experience: 123_456,
            Aptitude: PetAptitude.Godly,
            Rank: 25.25m,
            CompletedRebirths: 3,
            RebirthsRemaining: 2,
            CompletedPetMerges: 7,
            HasSoulContract: true,
            HasOwnerMergeTalent: true,
            CurrentEnergy: 90,
            MaximumEnergy: 100,
            Amity: 77,
            Satiety: 88,
            RemainingLifetime: 1_100,
            AvailableStatPoints: 9,
            GrowthRevealed: true,
            IsBound: true,
            ActivityState: "owned",
            IsCarried: false,
            IsSummoned: false,
            ContributesToCharacter: true,
            Revision: 12,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            StatValues:
            [
                Stat(1, 1.25m, 7.5m),
                Stat(2, 2.5m, 8.25m),
                Stat(3, 3.75m, 9m),
                Stat(4, 4m, 10.75m),
                Stat(5, 5.5m, 11.5m),
                Stat(6, 6.25m, 12.25m)
            ],
            CharacterBonuses: [],
            Skills:
            [
                new PetSkillSnapshot(5_200, 0, 1, 0, true, 1),
                new PetSkillSnapshot(6_000, 7, 3, 99, false, 2),
                new PetSkillSnapshot(5_555, 1, 2, 88, true, 3)
            ]);

    private static PetStatValueSnapshot Stat(
        short code,
        decimal initial,
        decimal added) =>
        new(code, initial, added, 0m, 0m, 1);

    private static GameCharacter CreateCharacter() =>
        new()
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = "test2",
            Camp = GameDefaults.SpartaCamp,
            Profession = 0,
            Level = 80,
            CurrentMap = GameDefaults.SpartaCapitalMap,
            PositionX = GameDefaults.StartingPositionX,
            PositionZ = GameDefaults.StartingPositionZ,
            CurrentHp = 5_000,
            MaxHp = 5_000,
            CurrentMp = 1_000,
            MaxMp = 1_000,
            Equipment = string.Empty,
            KitBag = string.Empty
        };

    private static async Task InvokePacketAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var task = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "GameClientHandler.HandlePacketAsync returned no task.");
        await task;
    }

    private static GamePacket CreateOpcodePacket(ushort opcode)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 4);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), opcode);
        return new GamePacket(bytes);
    }

    private static List<byte[]> SplitPackets(byte[] clearBytes)
    {
        var packets = new List<byte[]>();
        var offset = 0;
        while (offset < clearBytes.Length)
        {
            Check.True(
                clearBytes.Length - offset >= 4,
                "enter bootstrap has a complete packet header");
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                clearBytes.AsSpan(offset, 2));
            Check.True(
                length >= 4 && length <= clearBytes.Length - offset,
                "enter bootstrap packet has a bounded declared length");
            packets.Add(clearBytes.AsSpan(offset, length).ToArray());
            offset += length;
        }

        return packets;
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> source)
    {
        var terminator = source.IndexOf((byte)0);
        var length = terminator >= 0 ? terminator : source.Length;
        return Encoding.ASCII.GetString(source[..length]);
    }

    private static uint ToFixedPoint(decimal value) =>
        checked((uint)decimal.Round(
            value * 100m,
            0,
            MidpointRounding.AwayFromZero));

    private static ushort ReadUInt16(byte[] packet, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(offset, sizeof(ushort)));

    private static void SetField<T>(
        GameClientHandler handler,
        string name,
        T value)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        field.SetValue(handler, value);
    }

}
