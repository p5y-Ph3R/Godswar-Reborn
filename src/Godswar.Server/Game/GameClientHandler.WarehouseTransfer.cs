using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleWarehouseTransferAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }
        if (!WarehouseWireProtocol.TryReadTransfer(packet, out var intent))
        {
            await SendWarehouseTransferSecureResultAsync(
                packet.ClientOperationId,
                WarehouseTransferResultStatus.ConcurrentConflict,
                SecureLegacyCommandDisposition.Rejected,
                revision: 0,
                cancellationToken);
            return;
        }
        if (!TryAuthorizeWarehouseTransfer(out var warehouseNpc))
        {
            await SendWarehouseTransferSecureResultAsync(
                packet.ClientOperationId,
                WarehouseTransferResultStatus.ConcurrentConflict,
                SecureLegacyCommandDisposition.Rejected,
                revision: 0,
                cancellationToken);
            return;
        }
        if (_session.IsSecure && !packet.ClientOperationId.HasValue)
        {
            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.WarehouseTransfer);
            return;
        }
        if (!_session.IsSecure &&
            !AllowLegacyPlayerMutationFallback("warehouse_transfer"))
        {
            return;
        }
        if (!HasCurrentWarehouseRealmAuthority() ||
            !TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return;
        }
        if (_warehouseSnapshots is null ||
            _warehouseTransferCommands is null)
        {
            await SendWarehouseTransferSecureResultAsync(
                packet.ClientOperationId,
                WarehouseTransferResultStatus.ConcurrentConflict,
                SecureLegacyCommandDisposition.Rejected,
                revision: 0,
                cancellationToken);
            return;
        }

        var identity = _session.IsSecure
            ? WarehouseOperationIdentity.SecureClient(
                packet.ClientOperationId!.Value)
            : WarehouseOperationIdentity.RawLocalServer(
                Guid.NewGuid(),
                _commandConnectionId);
        var subject = new CommandSubject(_account.Id, _character.Id);
        var receivedAt = DateTimeOffset.UtcNow;
        WarehouseTransferExecutionResult execution;
        WarehouseTransferAuthoritativeState? initialState = null;
        WarehouseTransferResultStatus? localRejection = null;
        try
        {
            execution = identity.IsSecureClient
                ? await _warehouseTransferCommands.TryReplayAsync(
                    subject,
                    ownership,
                    ToWarehouseReplayIntent(
                        _processRealmId.Value,
                        intent),
                    identity,
                    cancellationToken)
                : WarehouseTransferExecutionResult.Terminal(
                    WarehouseTransferExecutionDisposition.ReplayNotFound);
            if (!RevalidateCurrentPlayerOwnership(ownership) ||
                !IsCurrentWarehouseNpc(warehouseNpc))
            {
                return;
            }

            if (execution.Disposition ==
                WarehouseTransferExecutionDisposition.ReplayNotFound)
            {
                initialState = await ReadWarehouseTransferStateAsync(
                    subject,
                    ownership,
                    cancellationToken);
                if (initialState is null ||
                    !RevalidateCurrentPlayerOwnership(ownership) ||
                    !IsCurrentWarehouseNpc(warehouseNpc))
                {
                    execution = WarehouseTransferExecutionResult.Terminal(
                        WarehouseTransferExecutionDisposition
                            .PreconditionFailed);
                }
                else if (!TryCreateWarehouseTransferCommand(
                             identity,
                             _processRealmId.Value,
                             intent,
                             initialState,
                             out var command,
                             out var rejection))
                {
                    localRejection = rejection;
                    execution = WarehouseTransferExecutionResult.Terminal(
                        WarehouseTransferExecutionDisposition.InvalidIntent);
                }
                else
                {
                    var envelope = WarehouseTransferCommandEnvelope.Create(
                        subject,
                        CreateWarehouseCommandCorrelation(identity),
                        receivedAt,
                        command) with { Ownership = ownership };
                    execution = await _warehouseTransferCommands.ExecuteAsync(
                        envelope,
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.WarehouseTransfer,
                identity.Strength,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        catch (Exception exception)
        {
            CommandMetrics.Record(
                CommandFamily.WarehouseTransfer,
                identity.Strength,
                CommandOutcome.ProviderUnavailable);
            Console.Error.WriteLine(
                "[warehouse] durable transfer remains pending: " +
                exception.Message);
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }
        if (localRejection.HasValue && initialState is not null)
        {
            CommandMetrics.Record(
                CommandFamily.WarehouseTransfer,
                identity.Strength,
                CommandOutcome.PreconditionFailed);
            ApplyWarehouseKitBagProjection(
                initialState.Character,
                initialState.Warehouse.InventoryRevision);
            await SendKitBagRefreshAsync(cancellationToken);
            await SendWarehouseSnapshotAsync(
                initialState.Warehouse,
                cancellationToken,
                "WarehouseTransferRejectedSnapshot");
            await SendWarehouseTransferSecureResultAsync(
                identity.IsSecureClient ? identity.OperationId : null,
                localRejection.Value,
                SecureLegacyCommandDisposition.Rejected,
                initialState.Warehouse.InventoryRevision,
                cancellationToken);
            return;
        }

        CommandMetrics.Record(
            CommandFamily.WarehouseTransfer,
            identity.Strength,
            MapWarehouseTransferOutcome(execution.Disposition));
        await CompleteWarehouseTransferAsync(
            subject,
            ownership,
            identity,
            intent,
            execution,
            cancellationToken);
    }

    private bool IsCurrentWarehouseNpc(NpcSpawnDefinition expected) =>
        TryResolveMapNpc(expected.InteractionId, out var current) &&
        string.Equals(
            current.NpcKey,
            expected.NpcKey,
            StringComparison.Ordinal) &&
        WarehouseNpcProtocol.IsWarehouseEndpoint(
            current.NpcKey,
            current.InteractionId);

    private static WarehouseTransferReplayIntent ToWarehouseReplayIntent(
        int realmId,
        in WarehouseTransferIntent intent) =>
        new(
            realmId,
            intent.Operation,
            intent.WarehouseSlot,
            intent.KitBagSlot,
            intent.DestinationWarehouseSlot,
            intent.Money,
            intent.StorageType);

    private static CommandOutcome MapWarehouseTransferOutcome(
        WarehouseTransferExecutionDisposition disposition) =>
        disposition switch
        {
            WarehouseTransferExecutionDisposition.Committed =>
                CommandOutcome.Accepted,
            WarehouseTransferExecutionDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            WarehouseTransferExecutionDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            WarehouseTransferExecutionDisposition.RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            WarehouseTransferExecutionDisposition.ReplayNotFound =>
                CommandOutcome.ProviderUnavailable,
            _ => CommandOutcome.PreconditionFailed
        };
}
