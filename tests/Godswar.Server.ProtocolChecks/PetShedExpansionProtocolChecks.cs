using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetShedExpansionProtocolChecks
{
    private const int ItemSlot = 25;

    public static async Task RunAsync()
    {
        CheckNativeResultCodec();
        await CheckSecureExpansionAsync();
        await CheckMaximumRejectionAsync();
        await CheckRawLocalExpansionAsync();
    }

    private static void CheckNativeResultCodec()
    {
        Check.True(
            PacketBuilder.PetShedExpansionResult(
                    PetShedExpansionResultCode.Succeeded)
                .SequenceEqual(new byte[] { 5, 0, 9, 40, 11 }),
            "pet shed success preserves captured opcode 10249 code 11");
        Check.True(
            PacketBuilder.PetShedExpansionResult(
                    PetShedExpansionResultCode.AlreadyMaximum)
                .SequenceEqual(new byte[] { 5, 0, 9, 40, 2 }),
            "pet shed maximum preserves captured opcode 10249 code 2");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetShedExpansionResult(
                (PetShedExpansionResultCode)0),
            "pet shed result rejects invented native codes");
    }

    private static async Task CheckSecureExpansionAsync()
    {
        var initial = CharacterWithShedItem();
        var updated = WithoutShedItem(initial);
        var operationId = Guid.NewGuid();
        var executor = SuccessExecutor();
        await using var fixture = PetDurableHandlerFixture.Create(
            initial,
            updated,
            [],
            executor,
            openedPetShedCells: 3);

        await fixture.InvokeAsync(CreateUsePacket(operationId));

        AssertProjection(
            fixture.Transport.ReadLegacyPackets(),
            updated,
            3,
            PetShedExpansionResultCode.Succeeded,
            "secure shed expansion");
        Check.True(
            executor.ActivationEnvelope is { } envelope &&
            envelope.Command.KitBagSlot == ItemSlot &&
            envelope.Command.ClientOperationId == operationId &&
            envelope.Subject is { AccountId: 13, CharacterId: 2 },
            "secure shed expansion binds the exact slot and operation");
        Check.True(
            fixture.Transport.CommandResults is
            [
                {
                    Disposition:
                        SecureLegacyCommandDisposition.Applied,
                    ResultCode:
                        (uint)PetDurableReceiptStatus.PetShedExpanded,
                    OperationId: var completed
                }
            ] && completed == operationId,
            "secure shed expansion terminates as applied");
    }

    private static async Task CheckMaximumRejectionAsync()
    {
        var character = CharacterWithShedItem();
        var operationId = Guid.NewGuid();
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Activate = envelope => PetDurableExecutionResult.Rejected(
                Receipt(
                    envelope,
                    PetDurableReceiptStatus.PetShedMaximumReached,
                    outboxEventId: null))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [],
            executor,
            openedPetShedCells:
                PetShedCapacityPolicy.MaximumOpenedCellCount);

        await fixture.InvokeAsync(CreateUsePacket(operationId));

        AssertProjection(
            fixture.Transport.ReadLegacyPackets(),
            character,
            PetShedCapacityPolicy.MaximumOpenedCellCount,
            PetShedExpansionResultCode.AlreadyMaximum,
            "maximum shed rejection");
        Check.True(
            fixture.Transport.CommandResults is
            [
                {
                    Disposition:
                        SecureLegacyCommandDisposition.Rejected,
                    ResultCode:
                        (uint)PetDurableReceiptStatus
                            .PetShedMaximumReached
                }
            ],
            "maximum shed rejection remains a terminal durable result");
    }

    private static async Task CheckRawLocalExpansionAsync()
    {
        var initial = CharacterWithShedItem();
        var updated = WithoutShedItem(initial);
        var executor = SuccessExecutor();
        await using var fixture = PetDurableRawHandlerFixture.Create(
            initial,
            updated,
            [],
            executor,
            hasLocalDevelopmentCapability: true,
            openedPetShedCells: 3);

        await fixture.InvokeAsync(CreateUsePacket(operationId: null));

        AssertProjection(
            fixture.ReadLegacyPackets(),
            updated,
            3,
            PetShedExpansionResultCode.Succeeded,
            "raw-local shed expansion");
        Check.True(
            executor.ActivationEnvelope is { } envelope &&
            envelope.IdentityStrength ==
                CommandIdentityStrength.ServerOperationId &&
            envelope.Command.Identity.IsRawLocalServer &&
            envelope.Command.KitBagSlot == ItemSlot,
            "raw-local shed expansion receives a connection-bound server UUID");
    }

    private static DelegatingPetDurableCommandExecutor SuccessExecutor() =>
        new()
        {
            Activate = envelope => PetDurableExecutionResult.Committed(
                Receipt(
                    envelope,
                    PetDurableReceiptStatus.PetShedExpanded,
                    Guid.NewGuid()))
        };

    private static PetDurableReceipt Receipt(
        CommandEnvelope<BagItemActivationCommand> envelope,
        PetDurableReceiptStatus status,
        Guid? outboxEventId) =>
        new(
            CommandFamily.BagItemActivation,
            status,
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
            AggregateRevision: status ==
                PetDurableReceiptStatus.PetShedExpanded ? 1 : 0,
            AuditReference: "pet-shed-expansion-check",
            OutboxEventId: outboxEventId);

    private static void AssertProjection(
        IReadOnlyList<byte[]> actual,
        GameCharacter character,
        short openedCells,
        PetShedExpansionResultCode result,
        string scope)
    {
        var expected = new[]
            {
                PacketBuilder.PetShedExpansionResult(result)
            }
            .Concat(result == PetShedExpansionResultCode.Succeeded
                ?
                [PacketBuilder.StorageItemKitBagDelete(ItemSlot)]
                : [])
            .Concat(PacketBuilder.KitBagDetailPages(character))
            .Concat(PacketBuilder.KitBagSlotIndexes(character))
            .Append(PacketBuilder.OwnedPetList(
                PetContentTestCatalog.Instance,
                [],
                openedCells))
            .ToArray();
        Check.Equal(expected.Length, actual.Count, $"{scope} frame count");
        for (var index = 0; index < expected.Length; index++)
        {
            Check.True(
                expected[index].SequenceEqual(actual[index]),
                $"{scope} native frame {index}");
        }
    }

    private static GameCharacter CharacterWithShedItem()
    {
        var item = CompactItemEntry.Parse(
            $"[{PetItemCatalog.SpecialPetShed},,,,,,0,1,0,1,0,0]");
        return new GameCharacter
        {
            Id = 2,
            AccountId = 13,
            Name = "test2",
            KitBag = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                ItemSlot,
                item.ToCompactString()),
            Equipment = GameDefaults.DefaultEquipment(1)
        };
    }

    private static GameCharacter WithoutShedItem(GameCharacter initial) =>
        new()
        {
            Id = initial.Id,
            AccountId = initial.AccountId,
            Name = initial.Name,
            Profession = initial.Profession,
            Equipment = initial.Equipment,
            KitBag = KitBagSlots.ClearSlot(initial.KitBag, ItemSlot)
        };

    private static GamePacket CreateUsePacket(Guid? operationId)
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
        return new GamePacket(packet, operationId);
    }
}
