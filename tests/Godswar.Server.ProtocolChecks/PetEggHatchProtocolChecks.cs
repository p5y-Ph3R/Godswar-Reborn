using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetEggHatchProtocolChecks
{
    private const int AccountId = 13;
    private const int CharacterId = 2;
    private const int EggSlot = 25;
    private const uint EggItemId = 10150;
    private const long PetId = 77;

    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    public static async Task RunAsync()
    {
        CheckEggCatalog();
        await CheckSuccessfulHatchAsync();
        await CheckRejectedHatchAsync();
    }

    private static void CheckEggCatalog()
    {
        Check.True(
            PetSpeciesCatalog.TryGetByEggItemId(
                EggItemId,
                out var rockElf) &&
            rockElf.Type == 1,
            "Rock Elf egg resolves through the authoritative catalog");
        Check.True(
            PetSpeciesCatalog.TryGetByEggItemId(
                10187,
                out var thunderPixie) &&
            thunderPixie.Type == 38 &&
            thunderPixie.EggDeclaredSpeciesType == 36,
            "late stock egg mismatch follows its displayed species");
        Check.True(
            !PetSpeciesCatalog.TryGetByEggItemId(
                10107,
                out _),
            "ordinary pet consumables are not eggs");
    }

    private static async Task CheckSuccessfulHatchAsync()
    {
        var initial = CharacterWithEgg(stack: 1);
        var updated = new GameCharacter
        {
            Id = initial.Id,
            AccountId = initial.AccountId,
            Name = initial.Name,
            Profession = initial.Profession,
            Equipment = initial.Equipment,
            KitBag = KitBagSlots.ClearSlot(
                initial.KitBag,
                EggSlot)
        };
        var growth = PetGrowthPolicy.Distribute(
            PetAptitude.Godly,
            50m,
            new Random(50));
        var addedSavvy = PetAddedSavvyPolicy.Distribute(
            PetAptitude.Godly,
            3_500,
            new Random(3_500));
        var pet = CreatePet(addedSavvy, growth);
        var store = new PetEggStore(
            new PetEggHatchResult(
                PetEggHatchStatus.Succeeded,
                updated,
                PetId,
                SpeciesType: 1,
                PetAptitude.Godly,
                growth.BaseGrowthRates,
                addedSavvy,
                growth),
            [pet]);

        var packets = await InvokeAsync(
            store,
            initial,
            CreateEggUsePacket(EggSlot));
        var expected = PacketBuilder
            .KitBagMutationDeletionAcknowledgements(
                initial.KitBag,
                updated.KitBag)
            .Concat(PacketBuilder.KitBagDetailPages(updated))
            .Concat(PacketBuilder.KitBagSlotIndexes(updated))
            .Append(PacketBuilder.OwnedPetList([pet]))
            .ToArray();

        Check.Equal(1, store.HatchCalls, "egg hatch persists once");
        Check.Equal(AccountId, store.AccountId, "egg hatch account");
        Check.Equal(CharacterId, store.CharacterId, "egg hatch character");
        Check.Equal(EggSlot, store.KitBagSlot, "egg hatch authoritative slot");
        Check.Equal(1, store.PetReads, "successful hatch reloads owned pets");
        Check.Equal(
            expected.Length,
            packets.Count,
            "hatch emits bounded bag and pet refresh frames");
        for (var index = 0; index < expected.Length; index++)
        {
            Check.True(
                expected[index].SequenceEqual(packets[index]),
                $"hatch refresh packet {index} preserves native order");
        }

        Check.True(
            packets.All(static packet =>
                ReadOpcode(packet) != Opcodes.PetOperationResult),
            "egg hatch does not misuse the carry/summon result opcode");
    }

    private static async Task CheckRejectedHatchAsync()
    {
        var character = CharacterWithEgg(stack: 1);
        var store = new PetEggStore(
            PetEggHatchResult.Rejected(
                PetEggHatchStatus.PetCapacityReached,
                character),
            []);

        var packets = await InvokeAsync(
            store,
            character,
            CreateEggUsePacket(EggSlot));
        var expected = PacketBuilder
            .KitBagDetailPages(character)
            .Concat(PacketBuilder.KitBagSlotIndexes(character))
            .ToArray();

        Check.Equal(1, store.HatchCalls, "capacity rejection reaches store");
        Check.Equal(0, store.PetReads, "rejected hatch does not reload pets");
        Check.Equal(
            expected.Length,
            packets.Count,
            "rejected hatch performs one authoritative bag resync");
        for (var index = 0; index < expected.Length; index++)
        {
            Check.True(
                expected[index].SequenceEqual(packets[index]),
                $"rejected hatch refresh packet {index}");
        }
    }

    private static async Task<List<byte[]>> InvokeAsync(
        PetEggStore store,
        GameCharacter character,
        GamePacket packet)
    {
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
            WorldContentReaderTestFixtures.Empty);
        SetField(
            handler,
            "_account",
            new GameAccount
            {
                Id = AccountId,
                Username = "test2"
            });
        SetField(handler, "_character", character);

        var task = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "GameClientHandler.HandlePacketAsync returned no task.");
        await task;

        var clearBytes = transport.WrittenBytes;
        new PacketCipher().Transform(clearBytes);
        return SplitPackets(clearBytes);
    }

    private static GamePacket CreateEggUsePacket(int slot)
    {
        var page = Math.DivRem(slot, 24, out var index);
        var packet = new byte[92];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 92);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.BreakItem);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(12),
            checked((ushort)page));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(14),
            checked((ushort)index));
        // The client also writes an item/action hint here. It is deliberately
        // wrong to prove routing and persistence use the authoritative slot.
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(72),
            uint.MaxValue);
        return new GamePacket(packet);
    }

    private static GameCharacter CharacterWithEgg(short stack)
    {
        var egg = CompactItemEntry.Parse(
            $"[{EggItemId},,,,,,{(short)PetAptitude.Godly},1,0,{stack},0,0]");
        return new GameCharacter
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = "test2",
            KitBag = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                EggSlot,
                egg.ToCompactString()),
            Equipment = GameDefaults.DefaultEquipment(1)
        };
    }

    private static PetBootstrapSnapshot CreatePet(
        PetAddedSavvyRoll addedSavvy,
        PetGrowthRoll growth) =>
        new(
            PetId,
            AccountId,
            CharacterId,
            SpeciesId: 1,
            Name: "Rock Elf",
            Sex: 0,
            Level: 1,
            Experience: 0,
            PetAptitude.Godly,
            Rank: 0m,
            CompletedRebirths: 0,
            RebirthsRemaining: 0,
            CompletedPetMerges: 0,
            HasSoulContract: false,
            HasOwnerMergeTalent: false,
            CurrentEnergy: 100,
            MaximumEnergy: 100,
            Amity: 100,
            Satiety: 100,
            RemainingLifetime: 600,
            AvailableStatPoints: 0,
            GrowthRevealed: false,
            IsBound: false,
            ActivityState: "owned",
            IsCarried: false,
            IsSummoned: false,
            ContributesToCharacter: false,
            Revision: 0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            StatValues:
            [
                new(
                    1,
                    growth.BaseGrowthRates.Agility,
                    addedSavvy.AddedSavvy.Agility,
                    growth.BaseGrowthRates.Agility,
                    0m,
                    0),
                new(
                    2,
                    growth.BaseGrowthRates.Strength,
                    addedSavvy.AddedSavvy.Strength,
                    growth.BaseGrowthRates.Strength,
                    0m,
                    0),
                new(
                    3,
                    growth.BaseGrowthRates.Accuracy,
                    addedSavvy.AddedSavvy.Accuracy,
                    growth.BaseGrowthRates.Accuracy,
                    0m,
                    0),
                new(
                    4,
                    growth.BaseGrowthRates.Technique,
                    addedSavvy.AddedSavvy.Technique,
                    growth.BaseGrowthRates.Technique,
                    0m,
                    0),
                new(
                    5,
                    growth.BaseGrowthRates.Wisdom,
                    addedSavvy.AddedSavvy.Wisdom,
                    growth.BaseGrowthRates.Wisdom,
                    0m,
                    0),
                new(
                    6,
                    growth.BaseGrowthRates.Luck,
                    addedSavvy.AddedSavvy.Luck,
                    growth.BaseGrowthRates.Luck,
                    0m,
                    0)
            ],
            CharacterBonuses: [],
            Skills:
            [
                new(
                    SkillId: 405,
                    SlotIndex: 0,
                    SkillRank: 1,
                    SkillExperience: 0,
                    IsActive: true,
                    Revision: 0)
            ]);

    private static List<byte[]> SplitPackets(byte[] clearBytes)
    {
        var packets = new List<byte[]>();
        var offset = 0;
        while (offset < clearBytes.Length)
        {
            Check.True(
                clearBytes.Length - offset >= 4,
                "pet-egg response has a complete header");
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                clearBytes.AsSpan(offset, 2));
            Check.True(
                length >= 4 &&
                length <= clearBytes.Length - offset,
                "pet-egg response has a bounded packet");
            packets.Add(
                clearBytes.AsSpan(offset, length).ToArray());
            offset += length;
        }

        return packets;
    }

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)));

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

    private sealed class PetEggStore : GameStoreTestStub
    {
        private readonly PetEggHatchResult _result;
        private readonly IReadOnlyList<PetBootstrapSnapshot> _pets;

        public PetEggStore(
            PetEggHatchResult result,
            IReadOnlyList<PetBootstrapSnapshot> pets)
        {
            _result = result;
            _pets = pets;
        }

        public int HatchCalls { get; private set; }

        public int PetReads { get; private set; }

        public int AccountId { get; private set; }

        public int CharacterId { get; private set; }

        public int KitBagSlot { get; private set; }

        public override Task<PetEggHatchResult> HatchPetEggAsync(
            int accountId,
            int characterId,
            int kitBagSlot,
            CancellationToken cancellationToken = default)
        {
            HatchCalls++;
            AccountId = accountId;
            CharacterId = characterId;
            KitBagSlot = kitBagSlot;
            return Task.FromResult(_result);
        }

        public override Task<IReadOnlyList<PetBootstrapSnapshot>>
            GetOwnedPetsAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default)
        {
            PetReads++;
            return Task.FromResult(_pets);
        }
    }
}
