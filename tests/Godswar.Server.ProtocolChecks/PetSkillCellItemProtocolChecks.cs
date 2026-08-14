using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetSkillCellItemProtocolChecks
{
    private const int ItemSlot = 25;
    private const short InitialStack = 99;

    public static async Task RunAsync()
    {
        CheckNativeSkillStateCodec();
        await CheckRawSpringUseAsync();
        await CheckRawSpringFinalUnitUseAsync();
        await CheckRawAppleUseAsync();
        await CheckAppleWithoutSealedCellAsync();
    }

    private static void CheckNativeSkillStateCodec()
    {
        var pet = CreatePet(
            openedSkillSlots: 1,
            availableSkillSlots: 2,
            revision: 1);
        var expected = Convert.FromHexString(
            "240007284D00000002010100950100000000000000000000000000000000000000000000");
        var actual = PacketBuilder.PetSkillState(pet);
        Check.Equal(36, actual.Length, "pet skill-state frame length");
        Check.True(
            expected.SequenceEqual(actual),
            "pet skill-state preserves the verified 36-byte opcode 10247 layout");
    }

    private static Task CheckRawSpringUseAsync() =>
        CheckRawSuccessfulUseAsync(
            PetItemCatalog.PetEnhanceSpring,
            PetDurableReceiptStatus.PetSkillCellMadeAvailable,
            initialOpened: 1,
            initialAvailable: 1,
            updatedOpened: 1,
            updatedAvailable: 2,
            "Pet Enhance Spring");

    private static Task CheckRawSpringFinalUnitUseAsync() =>
        CheckRawSuccessfulUseAsync(
            PetItemCatalog.PetEnhanceSpring,
            PetDurableReceiptStatus.PetSkillCellMadeAvailable,
            initialOpened: 1,
            initialAvailable: 1,
            updatedOpened: 1,
            updatedAvailable: 2,
            "final Pet Enhance Spring",
            initialStack: 1);

    private static Task CheckRawAppleUseAsync() =>
        CheckRawSuccessfulUseAsync(
            PetItemCatalog.GoldenAppleJuice,
            PetDurableReceiptStatus.PetSkillCellOpened,
            initialOpened: 1,
            initialAvailable: 2,
            updatedOpened: 2,
            updatedAvailable: 2,
            "Golden Apple Juice");

    private static async Task CheckRawSuccessfulUseAsync(
        uint itemId,
        PetDurableReceiptStatus status,
        short initialOpened,
        short initialAvailable,
        short updatedOpened,
        short updatedAvailable,
        string scope,
        short initialStack = InitialStack)
    {
        var initialCharacter = CharacterWithItem(itemId, initialStack);
        var updatedCharacter = initialStack == 1
            ? CharacterWithoutItem(initialCharacter)
            : CharacterWithItem(itemId, initialStack - 1);
        var initialPet = CreatePet(
            initialOpened,
            initialAvailable,
            revision: 7);
        var updatedPet = CreatePet(
            updatedOpened,
            updatedAvailable,
            revision: 8);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Activate = envelope =>
                PetDurableExecutionResult.Committed(
                    Receipt(envelope, status, updatedPet))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            initialCharacter,
            updatedCharacter,
            [updatedPet],
            executor,
            hasLocalDevelopmentCapability: true);

        await fixture.InvokeAsync(CreateUsePacket());

        var actual = fixture.ReadLegacyPackets();
        var expected = (initialStack == 1
                ? new[]
                {
                    PacketBuilder.StorageItemKitBagDelete(ItemSlot)
                }
                : [])
            .Concat(PacketBuilder.KitBagDetailPages(updatedCharacter))
            .Concat(PacketBuilder.KitBagSlotIndexes(updatedCharacter))
            .Append(PacketBuilder.PetSkillState(updatedPet))
            .ToArray();
        Check.Equal(1, executor.ActivateCount, $"{scope} persists once");
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
            $"{scope} receives a connection-bound server operation ID");
        Check.Equal(
            expected.Length,
            actual.Count,
            $"{scope} emits one bounded authoritative refresh");
        for (var index = 0; index < expected.Length; index++)
        {
            Check.True(
                expected[index].SequenceEqual(actual[index]),
                $"{scope} native response frame {index}");
        }
        Check.Equal(
            0,
            actual.Count(packet => ReadOpcode(packet) == 10_245),
            $"{scope} never sends the pet-care field-update packet");
        Check.Equal(
            1,
            actual.Count(packet => ReadOpcode(packet) == 10_247),
            $"{scope} refreshes the exact pet skill state once");
        Check.Equal(
            0,
            actual.Count(packet => ReadOpcode(packet) == 10_237),
            $"{scope} does not rebuild carry/summon presentation");
        Check.Equal(
            initialStack == 1 ? 1 : 0,
            actual.Count(packet =>
                ReadOpcode(packet) == Opcodes.StorageItem),
            initialStack == 1
                ? $"{scope} clears the authoritative empty source slot"
                : $"{scope} preserves the populated source slot for native cooling");

        // The fixture presents only the committed projection to the handler;
        // this assertion makes the intended transition explicit.
        Check.True(
            initialPet.OpenedSkillSlots == initialOpened &&
            initialPet.AvailableSkillSlots == initialAvailable &&
            updatedPet.OpenedSkillSlots == updatedOpened &&
            updatedPet.AvailableSkillSlots == updatedAvailable,
            $"{scope} advances only its intended cell boundary");
    }

    private static async Task CheckAppleWithoutSealedCellAsync()
    {
        var character = CharacterWithItem(
            PetItemCatalog.GoldenAppleJuice,
            InitialStack);
        var pet = CreatePet(
            openedSkillSlots: 1,
            availableSkillSlots: 1,
            revision: 7);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Activate = envelope =>
                PetDurableExecutionResult.Rejected(
                    Receipt(
                        envelope,
                        PetDurableReceiptStatus
                            .PetSkillCellNotAvailable,
                        pet,
                        succeeded: false))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            character,
            character,
            [pet],
            executor,
            hasLocalDevelopmentCapability: true);

        await fixture.InvokeAsync(CreateUsePacket());

        var actual = fixture.ReadLegacyPackets();
        var expected = PacketBuilder
            .KitBagDetailPages(character)
            .Concat(PacketBuilder.KitBagSlotIndexes(character))
            .Append(PacketBuilder.OwnedPetList(
                PetContentTestCatalog.Instance,
                [pet],
                PetShedCapacityPolicy.DefaultOpenedCellCount))
            .ToArray();
        Check.Equal(
            expected.Length,
            actual.Count,
            "unavailable Golden Apple Juice emits only repair projections");
        for (var index = 0; index < expected.Length; index++)
        {
            Check.True(
                expected[index].SequenceEqual(actual[index]),
                $"unavailable Golden Apple Juice response frame {index}");
        }
        Check.Equal(
            0,
            actual.Count(packet => packet.SequenceEqual(
                PacketBuilder.StorageItemKitBagDelete(ItemSlot))),
            "unavailable Golden Apple Juice does not clear or consume its source slot");
        Check.Equal(
            0,
            actual.Count(packet => ReadOpcode(packet) == 10_245),
            "unavailable Golden Apple Juice does not advertise a skill-cell mutation");
    }

    private static PetDurableReceipt Receipt(
        CommandEnvelope<BagItemActivationCommand> envelope,
        PetDurableReceiptStatus status,
        PetBootstrapSnapshot pet,
        bool succeeded = true) =>
        new(
            CommandFamily.BagItemActivation,
            status,
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
            AggregateRevision: succeeded ? 1 : 0,
            AuditReference: "pet-skill-cell-item-check",
            OutboxEventId: succeeded ? Guid.NewGuid() : null);

    private static GameCharacter CharacterWithItem(
        uint itemId,
        int stack)
    {
        var item = CompactItemEntry.Parse(
            $"[{itemId},,,,,,0,1,1,{stack},0,0]");
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

    private static GameCharacter CharacterWithoutItem(
        GameCharacter initial) =>
        new()
        {
            Id = initial.Id,
            AccountId = initial.AccountId,
            Name = initial.Name,
            Profession = initial.Profession,
            KitBag = KitBagSlots.ClearSlot(initial.KitBag, ItemSlot),
            Equipment = initial.Equipment
        };

    private static PetBootstrapSnapshot CreatePet(
        short openedSkillSlots,
        short availableSkillSlots,
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
            Revision = revision,
            OpenedSkillSlots = openedSkillSlots,
            AvailableSkillSlots = availableSkillSlots
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
