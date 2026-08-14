using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetEggHatchProtocolChecks
{
    internal const int AccountId = 13;
    internal const int CharacterId = 2;
    internal const int EggSlot = 25;
    internal const uint EggItemId = 10150;
    internal const long PetId = 77;

    public static async Task RunAsync()
    {
        CheckEggCatalog();
        await CheckSuccessfulHatchAsync();
        await CheckSummonedCompanionReplacementAsync();
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
        var savvy = PetInitialSavvyPolicy.Distribute(
            PetAptitude.Godly,
            3_500,
            new Random(3_500));
        var pet = CreatePet(savvy, growth) with
        {
            IsCarried = true
        };
        var operationId = Guid.NewGuid();
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Activate = envelope =>
                PetDurableExecutionResult.Committed(
                    new PetDurableReceipt(
                        CommandFamily.BagItemActivation,
                        PetDurableReceiptStatus.EggHatched,
                        envelope.Subject.AccountId,
                        envelope.Subject.CharacterId,
                        envelope.Command.KitBagSlot,
                        EquipmentSlot: -1,
                        PetId,
                        PetLevel: 1,
                        PetExperience: 0,
                        PetRevision: 0,
                        IsCarried: true,
                        IsSummoned: false,
                        PresenceOperation: 0,
                        AggregateRevision: 1,
                        AuditReference: "pet-hatch-check",
                        OutboxEventId: Guid.NewGuid()))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            initial,
            updated,
            [pet],
            executor);
        await fixture.InvokeAsync(
            CreateEggUsePacket(EggSlot, operationId));
        var packets = fixture.Transport.ReadLegacyPackets();
        var expected = new[]
            {
                PacketBuilder.StorageItemKitBagDelete(EggSlot)
            }
            .Concat(PacketBuilder.KitBagDetailPages(updated))
            .Concat(PacketBuilder.KitBagSlotIndexes(updated))
            .Append(PacketBuilder.OwnedPetList(
                PetContentTestCatalog.Instance,
                [pet],
                openedCellCount: 2))
            .Append(PacketBuilder.PetOperationResult(
                checked((uint)PetId),
                PetOperationResultCode.TakeSucceeded))
            .ToArray();

        Check.Equal(1, executor.ActivateCount, "egg hatch persists once");
        Check.True(
            executor.ActivationEnvelope is { } envelope &&
            envelope.Subject.AccountId == AccountId &&
            envelope.Subject.CharacterId == CharacterId &&
            envelope.Command.KitBagSlot == EggSlot &&
            envelope.Command.ClientOperationId == operationId,
            "egg hatch binds account, character, slot, and operation ID");
        Check.True(
            fixture.Transport.CommandResults is
            [
                {
                    Disposition:
                        SecureLegacyCommandDisposition.Applied,
                    CommandFamily: (ushort)CommandFamily.BagItemActivation,
                    OperationId: var completedOperation
                }
            ] &&
            completedOperation == operationId,
            "egg hatch terminates with one durable command result");
        Check.Equal(
            expected.Length + 2,
            packets.Count,
            "hatch emits bounded bag and pet refresh frames");
        for (var index = 0; index < expected.Length; index++)
        {
            Check.True(
                expected[index].SequenceEqual(packets[index]),
                $"hatch refresh packet {index} preserves native order");
        }
        Check.True(
            ReadOpcode(packets[^2]) == 10_167 &&
            ReadOpcode(packets[^1]) == 10_166,
            "hatch refreshes the new carried-skill source in 10167 then 10166 order");

        Check.Equal(
            1,
            packets.Count(static packet =>
                ReadOpcode(packet) == Opcodes.PetOperationResult),
            "egg hatch auto-carries the newly created pet exactly once");
    }

    private static async Task CheckRejectedHatchAsync()
    {
        var character = CharacterWithEgg(stack: 1);
        var growth = PetGrowthPolicy.Distribute(
            PetAptitude.Godly,
            50m,
            new Random(51));
        var savvy = PetInitialSavvyPolicy.Distribute(
            PetAptitude.Godly,
            3_500,
            new Random(3_501));
        var pets = new[]
        {
            CreatePet(savvy, growth) with
            {
                PetId = 71,
                Name = "First pet",
                IsCarried = true
            },
            CreatePet(savvy, growth) with
            {
                PetId = 72,
                Name = "Second pet"
            }
        };
        var operationId = Guid.NewGuid();
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Activate = envelope =>
                PetDurableExecutionResult.Rejected(
                    new PetDurableReceipt(
                        CommandFamily.BagItemActivation,
                        PetDurableReceiptStatus.PetCapacityReached,
                        envelope.Subject.AccountId,
                        envelope.Subject.CharacterId,
                        envelope.Command.KitBagSlot,
                        EquipmentSlot: -1,
                        PetId: 0,
                        PetLevel: 0,
                        PetExperience: 0,
                        PetRevision: 0,
                        IsCarried: false,
                        IsSummoned: false,
                        PresenceOperation: 0,
                        AggregateRevision: 0,
                        AuditReference: "pet-capacity-check",
                        OutboxEventId: null))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            pets,
            executor);
        await fixture.InvokeAsync(
            CreateEggUsePacket(EggSlot, operationId));
        var packets = fixture.Transport.ReadLegacyPackets();
        var expected = PacketBuilder
            .KitBagDetailPages(character)
            .Concat(PacketBuilder.KitBagSlotIndexes(character))
            .Append(PacketBuilder.OwnedPetList(
                PetContentTestCatalog.Instance,
                pets,
                openedCellCount: 2))
            .ToArray();

        Check.Equal(
            1,
            executor.ActivateCount,
            "capacity rejection reaches the durable executor");
        Check.True(
            fixture.Transport.CommandResults is
            [
                {
                    Disposition:
                        SecureLegacyCommandDisposition.Rejected,
                    ResultCode:
                        (uint)PetDurableReceiptStatus.PetCapacityReached,
                    OperationId: var completedOperation
                }
            ] &&
            completedOperation == operationId,
            "capacity rejection returns its durable terminal result");
        Check.Equal(
            expected.Length,
            packets.Count,
            "rejected hatch performs one authoritative bag resync");
        Check.Equal(
            0,
            packets.Count(static packet =>
                ReadOpcode(packet) == Opcodes.PetOperationResult),
            "capacity rejection never changes the carried or summoned pet");
        for (var index = 0; index < expected.Length; index++)
        {
            Check.True(
                expected[index].SequenceEqual(packets[index]),
                $"rejected hatch refresh packet {index}");
        }
    }

    private static GamePacket CreateEggUsePacket(
        int slot,
        Guid operationId)
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
        return new GamePacket(packet, operationId);
    }

    internal static GameCharacter CharacterWithEgg(short stack)
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

    internal static PetBootstrapSnapshot CreatePet(
        PetInitialSavvyRoll savvy,
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
                    savvy.InitialSavvy.Agility,
                    growth.BaseGrowthRates.Agility,
                    growth.BaseGrowthRates.Agility,
                    0m,
                    0),
                new(
                    2,
                    savvy.InitialSavvy.Strength,
                    growth.BaseGrowthRates.Strength,
                    growth.BaseGrowthRates.Strength,
                    0m,
                    0),
                new(
                    3,
                    savvy.InitialSavvy.Accuracy,
                    growth.BaseGrowthRates.Accuracy,
                    growth.BaseGrowthRates.Accuracy,
                    0m,
                    0),
                new(
                    4,
                    savvy.InitialSavvy.Technique,
                    growth.BaseGrowthRates.Technique,
                    growth.BaseGrowthRates.Technique,
                    0m,
                    0),
                new(
                    5,
                    savvy.InitialSavvy.Wisdom,
                    growth.BaseGrowthRates.Wisdom,
                    growth.BaseGrowthRates.Wisdom,
                    0m,
                    0),
                new(
                    6,
                    savvy.InitialSavvy.Luck,
                    growth.BaseGrowthRates.Luck,
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

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)));

}
