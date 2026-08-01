using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Networking.Secure;
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
                        IsCarried: false,
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
        var expected = PacketBuilder
            .KitBagDetailPages(updated)
            .Concat(PacketBuilder.KitBagSlotIndexes(updated))
            .Append(PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance, [pet]))
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
            [],
            executor);
        await fixture.InvokeAsync(
            CreateEggUsePacket(EggSlot, operationId));
        var packets = fixture.Transport.ReadLegacyPackets();
        var expected = PacketBuilder
            .KitBagDetailPages(character)
            .Concat(PacketBuilder.KitBagSlotIndexes(character))
            .Append(PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance, []))
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

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)));

}
