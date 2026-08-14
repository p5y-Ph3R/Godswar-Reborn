using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetExperienceItemProtocolChecks
{
    private const int ItemSlot = 25;
    private const int ItemStack = 99;

    public static async Task RunAsync()
    {
        CheckNativePetExperienceCodec();
        await CheckRawMorningDewUseAsync();
    }

    private static void CheckNativePetExperienceCodec()
    {
        var expected = Convert.FromHexString(
            "0C0015284D00000040E20100");
        Check.True(
            PacketBuilder.PetExperience(77, 123_456)
                .SequenceEqual(expected),
            "pet EXP preserves the recovered 12-byte opcode 10261 layout");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetExperience(0, 1),
            "pet EXP rejects a non-native pet ID");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetExperience(1, (long)uint.MaxValue + 1),
            "pet EXP rejects a non-native total");
    }

    private static async Task CheckRawMorningDewUseAsync()
    {
        var initialCharacter = CharacterWithMorningDew(ItemStack);
        var updatedCharacter = CharacterWithMorningDew(ItemStack - 1);
        var pet = CreatePet(experience: 123_456, revision: 7);
        var updatedPet = pet with
        {
            Experience = pet.Experience + 10_000_000,
            Revision = pet.Revision + 1,
            HasOwnerMergeTalent = true,
            TalentMask = 16,
            IsSummoned = true,
            ContributesToCharacter = true
        };
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Activate = envelope =>
                PetDurableExecutionResult.Committed(
                    Receipt(envelope, updatedPet))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            initialCharacter,
            updatedCharacter,
            [updatedPet],
            executor,
            hasLocalDevelopmentCapability: true);

        await fixture.InvokeAsync(CreateUsePacket());

        var actual = fixture.ReadLegacyPackets();
        var expected = PacketBuilder
            .KitBagDetailPages(updatedCharacter)
            .Concat(PacketBuilder.KitBagSlotIndexes(updatedCharacter))
            .Append(PacketBuilder.PetExperience(
                updatedPet.PetId,
                updatedPet.Experience))
            .ToArray();
        Check.Equal(
            1,
            executor.ActivateCount,
            "raw Morning Dew activation persists once");
        Check.True(
            executor.ActivationEnvelope is { } envelope &&
            envelope.IdentityStrength ==
                CommandIdentityStrength.ServerOperationId &&
            envelope.Connection.Transport ==
                CommandTransportKind.LegacyTcp &&
            envelope.Command.Identity.IsRawLocalServer &&
            envelope.Command.Identity.OperationId != Guid.Empty &&
            envelope.Command.Identity.RawLocalConnectionId ==
                envelope.Connection.ConnectionId &&
            envelope.Command.KitBagSlot == ItemSlot,
            "raw Morning Dew receives a connection-bound operation ID");
        Check.Equal(
            expected.Length,
            actual.Count,
            "Morning Dew emits one authoritative bag and pet refresh");
        for (var index = 0; index < expected.Length; index++)
        {
            Check.True(
                expected[index].SequenceEqual(actual[index]),
                $"Morning Dew native response frame {index}");
        }
        Check.Equal(
            1,
            actual.Count(packet =>
                ReadOpcode(packet) == Opcodes.PetExperience),
            "Morning Dew refreshes the carried pet EXP once");
        Check.Equal(
            0,
            actual.Count(packet => ReadOpcode(packet) == 10_237),
            "Morning Dew does not rebuild or recall an actively merged pet");
        Check.Equal(
            0,
            actual.Count(packet =>
                ReadOpcode(packet) == Opcodes.StorageItem),
            "a surviving Morning Dew stack is not cleared");
    }

    private static PetDurableReceipt Receipt(
        CommandEnvelope<BagItemActivationCommand> envelope,
        PetBootstrapSnapshot pet) =>
        new(
            CommandFamily.BagItemActivation,
            PetDurableReceiptStatus.PetExperienceAdded,
            envelope.Subject.AccountId,
            envelope.Subject.CharacterId,
            envelope.Command.KitBagSlot,
            EquipmentSlot: -1,
            pet.PetId,
            pet.Level,
            pet.Experience,
            pet.Revision,
            pet.IsCarried,
            pet.IsSummoned,
            PresenceOperation: 0,
            AggregateRevision: 1,
            AuditReference: "pet-experience-item-check",
            OutboxEventId: Guid.NewGuid());

    private static GameCharacter CharacterWithMorningDew(int stack)
    {
        var item = CompactItemEntry.Parse(
            $"[{PetExperienceItemPolicy.LastMorningDew},,,,,,0,1,1,{stack},0,0]");
        return new GameCharacter
        {
            Id = PetEggHatchProtocolChecks.CharacterId,
            AccountId = PetEggHatchProtocolChecks.AccountId,
            Name = "test2",
            KitBag = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                ItemSlot,
                item.ToCompactString()),
            Equipment = GameDefaults.DefaultEquipment(1)
        };
    }

    private static PetBootstrapSnapshot CreatePet(
        long experience,
        long revision)
    {
        var growth = PetGrowthPolicy.Distribute(
            PetAptitude.Godly,
            50m,
            new Random(50));
        var savvy = PetInitialSavvyPolicy.Distribute(
            PetAptitude.Godly,
            3_500,
            new Random(3_500));
        return PetEggHatchProtocolChecks.CreatePet(
            savvy,
            growth) with
        {
            IsCarried = true,
            Experience = experience,
            Revision = revision
        };
    }

    private static GamePacket CreateUsePacket()
    {
        var page = Math.DivRem(ItemSlot, 24, out var index);
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
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(72),
            uint.MaxValue);
        return new GamePacket(packet);
    }

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)));
}
