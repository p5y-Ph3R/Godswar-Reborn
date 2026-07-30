using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Pets;
using Godswar.Server.Networking.Secure;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class EquipmentBagTransferDurableHandlerChecks
{
    private static async Task
        CheckActiveRideBlocksDurableRightClickMountAsync()
    {
        var petExecutor = new PetActivationExecutor();
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound(),
            // Deliberately stale: the live bag projection contains a weapon,
            // while the durable executor represents the locked DB item as a
            // mount. Runtime policy must not classify from this cache.
            liveState: UnequipAfterState,
            persistedState: UnequipAfterState,
            equipmentSlot: EquipmentSlots.Mount,
            petDurableCommands: petExecutor);
        await ActivateRideRuntimeStatusAsync(fixture);

        await InvokePacketAsync(
            fixture.Handler,
            CreateBreakItemPacket(OperationId));

        Check.Equal(
            1,
            petExecutor.ExecuteCount,
            "Ride-active right-click decision reaches durable persistence");
        Check.True(
            petExecutor.ExecutedCommand?.ExecutionConstraint ==
                BagItemActivationExecutionConstraint
                    .RideRuntimeBlocked,
            "Ride observation is bound independently of stale cached item classification");
        Check.Equal(
            1,
            fixture.Transport.CommandResults.Count,
            "Ride-active right-click mount replacement terminates once");
        var result = fixture.Transport.CommandResults[0];
        Check.True(
            result.Disposition ==
                SecureLegacyCommandDisposition.Rejected &&
            result.CommandFamily ==
                (ushort)CommandFamily.BagItemActivation &&
            result.ResultCode ==
                (uint)PetDurableReceiptStatus.EquipmentRestricted &&
            result.AuthoritativeRevision == 0 &&
            result.OperationId == OperationId,
            "Ride-active right-click mount replacement returns a finite family-26 rejection");
    }

    private sealed class PetActivationExecutor :
        IPetDurableCommandExecutor
    {
        public int ExecuteCount { get; private set; }
        public BagItemActivationCommand? ExecutedCommand
        { get; private set; }

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<BagItemActivationCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            ExecutedCommand = envelope.Command;
            return Task.FromResult(
                PetDurableExecutionResult.Rejected(
                    new PetDurableReceipt(
                        CommandFamily.BagItemActivation,
                        PetDurableReceiptStatus.EquipmentRestricted,
                        envelope.Subject.AccountId,
                        envelope.Subject.CharacterId,
                        envelope.Command.KitBagSlot,
                        EquipmentSlots.Mount,
                        PetId: 0,
                        PetLevel: 0,
                        PetExperience: 0,
                        PetRevision: 0,
                        IsCarried: false,
                        IsSummoned: false,
                        PresenceOperation: 0,
                        AggregateRevision: 0,
                        AuditReference: "ride-runtime-check",
                        OutboxEventId: null)));
        }

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<PetLevelUpgradeCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The right-click mount check cannot upgrade pets.");

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<PetPresenceTransitionCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The right-click mount check cannot change pet presence.");
    }
}
