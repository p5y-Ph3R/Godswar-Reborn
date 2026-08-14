using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetOwnerMergeNativeRequestChecks
{
    public static async Task RunAsync()
    {
        CheckOpcode();
        await CheckSecureRequestNeedsNoBagItemAsync();
        await CheckMalformedRequestIsRejectedAsync();
        await CheckTokenlessSecureRequestIsRejectedAsync();
        await CheckRawLocalRequestUsesScopedIdentityAsync();
        await CheckRawRequestRequiresLocalCapabilityAsync();
    }

    private static void CheckOpcode()
    {
        Check.Equal(
            (ushort)10274,
            Opcodes.PetOwnerMergeRequest,
            "stock pet owner-Merge request opcode");
        Check.Equal(
            nameof(Opcodes.PetOwnerMergeRequest),
            Opcodes.Name(Opcodes.PetOwnerMergeRequest),
            "owner-Merge opcode diagnostic name");
    }

    private static async Task CheckSecureRequestNeedsNoBagItemAsync()
    {
        var operationId = Guid.NewGuid();
        var executor = RejectingExecutor();
        await using var fixture = PetDurableHandlerFixture.Create(
            Character(),
            Character(),
            [],
            executor);

        await fixture.InvokeAsync(CreateRequest(operationId));

        Check.True(
            executor.ToggleOwnerMergeEnvelope is { } envelope &&
            executor.ToggleOwnerMergeCount == 1 &&
            executor.ActivateCount == 0 &&
            envelope.Family == CommandFamily.PetOwnerMergeToggle &&
            envelope.Command.Identity.IsSecureClient &&
            envelope.Command.ClientOperationId == operationId &&
            envelope.Subject is { AccountId: 13, CharacterId: 2 },
            "secure Merge is an innate-pet command independent of bag items");
        Check.True(
            fixture.Transport.CommandResults is
            [
                {
                    Disposition:
                        SecureLegacyCommandDisposition.Rejected,
                    CommandFamily:
                        (ushort)CommandFamily.PetOwnerMergeToggle,
                    ResultCode:
                        (uint)PetDurableReceiptStatus
                            .OwnerMergeEnergyNotFull,
                    OperationId: var completed
                }
            ] && completed == operationId,
            "secure Merge completes through its dedicated durable result family");
    }

    private static async Task CheckMalformedRequestIsRejectedAsync()
    {
        var executor = RejectingExecutor();
        await using var fixture = PetDurableHandlerFixture.Create(
            Character(), Character(), [], executor);

        await fixture.InvokeAsync(CreateRequest(Guid.NewGuid(), 0x7F));

        Check.Equal(
            0,
            executor.ToggleOwnerMergeCount,
            "Merge rejects any payload beyond the exact four-byte header");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            "malformed Merge does not fabricate a durable result");
    }

    private static async Task CheckTokenlessSecureRequestIsRejectedAsync()
    {
        var executor = RejectingExecutor();
        await using var fixture = PetDurableHandlerFixture.Create(
            Character(), Character(), [], executor);

        await fixture.InvokeAsync(CreateRequest(operationId: null));

        Check.Equal(
            0,
            executor.ToggleOwnerMergeCount,
            "secure Merge cannot downgrade to a server-generated identity");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            "tokenless secure Merge cannot complete another operation");
    }

    private static async Task CheckRawLocalRequestUsesScopedIdentityAsync()
    {
        var executor = RejectingExecutor();
        await using var fixture = PetDurableRawHandlerFixture.Create(
            Character(),
            Character(),
            [],
            executor,
            hasLocalDevelopmentCapability: true);

        await fixture.InvokeAsync(CreateRequest(operationId: null));

        Check.True(
            executor.ToggleOwnerMergeEnvelope is { } envelope &&
            executor.ToggleOwnerMergeCount == 1 &&
            executor.ActivateCount == 0 &&
            envelope.IdentityStrength ==
                CommandIdentityStrength.ServerOperationId &&
            envelope.Command.Identity.IsRawLocalServer &&
            envelope.Command.Identity.OperationId != Guid.Empty &&
            envelope.Command.Identity.RawLocalConnectionId ==
                envelope.Connection.ConnectionId,
            "raw-local Merge uses a fresh operation scoped to its validated TCP connection");
    }

    private static async Task CheckRawRequestRequiresLocalCapabilityAsync()
    {
        var executor = RejectingExecutor();
        await using var fixture = PetDurableRawHandlerFixture.Create(
            Character(),
            Character(),
            [],
            executor,
            hasLocalDevelopmentCapability: false);

        await fixture.InvokeAsync(CreateRequest(operationId: null));

        Check.Equal(
            0,
            executor.ToggleOwnerMergeCount,
            "production raw Merge cannot reach durable persistence");
        Check.Equal(
            1,
            fixture.Transport.DisconnectCount,
            "production raw Merge is rejected by the legacy mutation fence");
    }

    private static DelegatingPetDurableCommandExecutor RejectingExecutor() =>
        new()
        {
            ToggleOwnerMerge = envelope =>
                PetDurableExecutionResult.Rejected(
                    new PetDurableReceipt(
                        CommandFamily.PetOwnerMergeToggle,
                        PetDurableReceiptStatus.OwnerMergeEnergyNotFull,
                        envelope.Subject.AccountId,
                        envelope.Subject.CharacterId,
                        KitBagSlot: -1,
                        EquipmentSlot: -1,
                        PetId: 1,
                        PetLevel: 120,
                        PetExperience: 0,
                        PetRevision: 1,
                        IsCarried: true,
                        IsSummoned: true,
                        PresenceOperation: 0,
                        AggregateRevision: 1,
                        AuditReference: "native-owner-merge-request-check",
                        OutboxEventId: null))
        };

    private static GameCharacter Character() =>
        new()
        {
            Id = 2,
            AccountId = 13,
            Name = "test2",
            Profession = 1,
            KitBag = GameDefaults.EmptyKitBag,
            Equipment = GameDefaults.DefaultEquipment(1)
        };

    private static GamePacket CreateRequest(
        Guid? operationId,
        byte? unexpectedPayload = null)
    {
        var packet = new byte[unexpectedPayload.HasValue ? 5 : 4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PetOwnerMergeRequest);
        if (unexpectedPayload is { } value)
        {
            packet[4] = value;
        }
        return new GamePacket(packet, operationId);
    }
}
