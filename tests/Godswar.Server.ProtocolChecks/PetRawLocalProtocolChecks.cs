using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetRawLocalProtocolChecks
{
    public static async Task RunAsync()
    {
        await CheckRawLocalHatchAsync();
        await CheckRawHatchRequiresLocalCapabilityAsync();
    }

    private static async Task CheckRawLocalHatchAsync()
    {
        var initial = PetEggHatchProtocolChecks.CharacterWithEgg(stack: 1);
        var updated = new GameCharacter
        {
            Id = initial.Id,
            AccountId = initial.AccountId,
            Name = initial.Name,
            Profession = initial.Profession,
            Equipment = initial.Equipment,
            KitBag = KitBagSlots.ClearSlot(
                initial.KitBag,
                PetEggHatchProtocolChecks.EggSlot)
        };
        var growth = PetGrowthPolicy.Distribute(
            PetAptitude.Godly,
            50m,
            new Random(50));
        var savvy = PetInitialSavvyPolicy.Distribute(
            PetAptitude.Godly,
            3_500,
            new Random(3_500));
        var pet = PetEggHatchProtocolChecks.CreatePet(
            savvy,
            growth) with
        {
            IsCarried = true
        };
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Activate = envelope =>
                PetDurableExecutionResult.Committed(
                    SuccessfulHatchReceipt(envelope))
        };

        await using var fixture = PetDurableRawHandlerFixture.Create(
            initial,
            updated,
            [pet],
            executor,
            hasLocalDevelopmentCapability: true);
        await fixture.InvokeAsync(
            CreateRawEggUsePacket(PetEggHatchProtocolChecks.EggSlot));

        var packets = fixture.ReadLegacyPackets();
        var expected = new[]
            {
                PacketBuilder.StorageItemKitBagDelete(
                    PetEggHatchProtocolChecks.EggSlot)
            }
            .Concat(PacketBuilder.KitBagDetailPages(updated))
            .Concat(PacketBuilder.KitBagSlotIndexes(updated))
            .Append(PacketBuilder.OwnedPetList(
                PetContentTestCatalog.Instance,
                [pet],
                openedCellCount: 2))
            .Append(PacketBuilder.PetOperationResult(
                checked((uint)PetEggHatchProtocolChecks.PetId),
                PetOperationResultCode.TakeSucceeded))
            .ToArray();
        Check.Equal(
            1,
            executor.ActivateCount,
            "raw-local hatch executes once");
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
            envelope.Command.KitBagSlot ==
                PetEggHatchProtocolChecks.EggSlot,
            "raw-local hatch binds a server UUID to the exact TCP connection");
        Check.True(
            !fixture.Session.IsSecure,
            "raw-local hatch cannot use the secure command-result channel");
        Check.Equal(
            expected.Length + 2,
            packets.Count,
            "raw-local hatch emits only native bag and pet projections");
        for (var index = 0; index < expected.Length; index++)
        {
            Check.True(
                expected[index].SequenceEqual(packets[index]),
                $"raw-local hatch native projection {index}");
        }
        Check.True(
            BinaryPrimitives.ReadUInt16LittleEndian(
                packets[^2].AsSpan(2, 2)) == 10_167 &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                packets[^1].AsSpan(2, 2)) == 10_166,
            "raw-local hatch terminates with 10167 then 10166 carried-skill stats");
    }

    private static async Task CheckRawHatchRequiresLocalCapabilityAsync()
    {
        var character =
            PetEggHatchProtocolChecks.CharacterWithEgg(stack: 1);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Activate = _ => throw new InvalidOperationException(
                "A raw pet hatch without local capability executed.")
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            character,
            character,
            [],
            executor,
            hasLocalDevelopmentCapability: false);

        await fixture.InvokeAsync(
            CreateRawEggUsePacket(PetEggHatchProtocolChecks.EggSlot));

        Check.Equal(
            0,
            executor.ActivateCount,
            "raw hatch without local capability cannot reach persistence");
        Check.Equal(
            1,
            fixture.Transport.DisconnectCount,
            "raw hatch without local capability disconnects");
        Check.Equal(
            0,
            fixture.Transport.WrittenBytes.Length,
            "blocked raw hatch emits no misleading success projection");
    }

    private static PetDurableReceipt SuccessfulHatchReceipt(
        CommandEnvelope<BagItemActivationCommand> envelope) =>
        new(
            CommandFamily.BagItemActivation,
            PetDurableReceiptStatus.EggHatched,
            envelope.Subject.AccountId,
            envelope.Subject.CharacterId,
            envelope.Command.KitBagSlot,
            EquipmentSlot: -1,
            PetEggHatchProtocolChecks.PetId,
            PetLevel: 1,
            PetExperience: 0,
            PetRevision: 0,
            IsCarried: true,
            IsSummoned: false,
            PresenceOperation: 0,
            AggregateRevision: 1,
            AuditReference: "raw-local-pet-hatch-check",
            OutboxEventId: Guid.NewGuid());

    private static GamePacket CreateRawEggUsePacket(int slot)
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
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(72),
            PetEggHatchProtocolChecks.EggItemId);
        return new GamePacket(packet);
    }
}
