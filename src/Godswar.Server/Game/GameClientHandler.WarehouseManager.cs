using System.Buffers.Binary;
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
    private readonly IWarehouseSnapshotReader? _warehouseSnapshots;
    private readonly IWarehouseTransferCommandExecutor?
        _warehouseTransferCommands;
    private readonly IWarehouseExpansionCommandExecutor?
        _warehouseExpansionCommands;
    private readonly WarehouseExpansionPolicySnapshot?
        _warehouseExpansionPolicy;

    private async Task HandleWarehouseManagerAsync(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }
        if (!IsExactWarehouseManagerRequest(
                packet,
                route,
                npcId,
                dialogIndex,
                subId,
                arguments))
        {
            await SendWarehouseExpansionRejectedAsync(
                npcId,
                WarehouseExpansionExecutionDisposition.InvalidIntent,
                packet.ClientOperationId,
                cancellationToken);
            return;
        }
        if (_session.IsSecure && !packet.ClientOperationId.HasValue)
        {
            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.WarehouseExpansion);
            await SendWarehouseExpansionRejectedAsync(
                npcId,
                WarehouseExpansionExecutionDisposition.InvalidIntent,
                clientOperationId: null,
                cancellationToken);
            return;
        }
        if (!_session.IsSecure &&
            !AllowLegacyPlayerMutationFallback("warehouse_expansion"))
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
            _warehouseExpansionCommands is null ||
            !TryGetWarehouseExpansionPolicy(out var policy))
        {
            await SendWarehouseExpansionRejectedAsync(
                npcId,
                WarehouseExpansionExecutionDisposition.PreconditionFailed,
                packet.ClientOperationId,
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
        WarehouseExpansionExecutionResult execution;
        try
        {
            execution = identity.IsSecureClient
                ? await _warehouseExpansionCommands.TryReplayAsync(
                    subject,
                    ownership,
                    new WarehouseExpansionReplayIntent(
                        _processRealmId.Value,
                        WarehouseExpansionCommandEnvelope.ActionSubId),
                    identity,
                    cancellationToken)
                : WarehouseExpansionExecutionResult.Terminal(
                    WarehouseExpansionExecutionDisposition.ReplayNotFound);
            if (!RevalidateCurrentPlayerOwnership(ownership))
            {
                return;
            }
            if (execution.Disposition ==
                WarehouseExpansionExecutionDisposition.ReplayNotFound)
            {
                execution = await ExecuteWarehouseExpansionAsync(
                    subject,
                    ownership,
                    identity,
                    npcId,
                    receivedAt,
                    policy,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.WarehouseExpansion,
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
                CommandFamily.WarehouseExpansion,
                identity.Strength,
                CommandOutcome.ProviderUnavailable);
            Console.Error.WriteLine(
                "[warehouse-manager] durable command remains pending: " +
                exception.Message);
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }
        CommandMetrics.Record(
            CommandFamily.WarehouseExpansion,
            identity.Strength,
            MapWarehouseExpansionOutcome(execution.Disposition));
        if (!execution.IsDurable)
        {
            await SendWarehouseExpansionRejectedAsync(
                npcId,
                execution.Disposition,
                identity.IsSecureClient ? identity.OperationId : null,
                cancellationToken);
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A durable warehouse expansion result has no receipt.");
        receipt.Validate();
        ValidateWarehouseExpansionReceipt(
            _character.Id,
            _processRealmId.Value,
            receipt,
            execution.Disposition,
            policy);
        if (receipt.Succeeded)
        {
            await ReloadWarehouseKitBagProjectionAsync(
                ownership,
                receipt.InventoryRevision,
                cancellationToken);
        }
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }
        await SendWarehouseExpansionResultAsync(
            npcId,
            identity,
            execution,
            receipt,
            cancellationToken);
    }

    private async Task<WarehouseExpansionExecutionResult>
        ExecuteWarehouseExpansionAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        WarehouseOperationIdentity identity,
        uint npcId,
        DateTimeOffset receivedAt,
        WarehouseExpansionPolicySnapshot policy,
        CancellationToken cancellationToken)
    {
        var snapshot = await _warehouseSnapshots!.ReadAsync(
            subject,
            ownership,
            cancellationToken);
        if (snapshot is null ||
            !RevalidateCurrentPlayerOwnership(ownership))
        {
            return WarehouseExpansionExecutionResult.Terminal(
                WarehouseExpansionExecutionDisposition.PreconditionFailed);
        }
        snapshot.Validate();
        if (!WarehouseExpansionCommandEnvelope.TryCreateCommand(
                identity,
                _processRealmId.Value,
                checked((int)npcId),
                WarehouseExpansionCommandEnvelope.DialogIndex,
                WarehouseExpansionCommandEnvelope.ActionSubId,
                snapshot.Capacity,
                policy,
                out var command))
        {
            return WarehouseExpansionExecutionResult.Terminal(
                WarehouseExpansionExecutionDisposition.InvalidIntent);
        }

        var envelope = WarehouseExpansionCommandEnvelope.Create(
            subject,
            CreateWarehouseCommandCorrelation(identity),
            receivedAt,
            command) with { Ownership = ownership };
        return await _warehouseExpansionCommands!.ExecuteAsync(
            envelope,
            cancellationToken);
    }

    private static bool IsExactWarehouseManagerRequest(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments) =>
        packet.Length == 92 &&
        packet.Buffer.Length == 92 &&
        route.Behavior == NpcDialogueBehavior.WarehouseManager &&
        route.DialogIndex == WarehouseNpcProtocol.ManagerDialogIndex &&
        WarehouseNpcProtocol.IsManagerEndpoint(route.NpcKey, npcId) &&
        dialogIndex == WarehouseNpcProtocol.ManagerDialogIndex &&
        BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(8, sizeof(int))) == dialogIndex &&
        subId == WarehouseNpcProtocol.ManagerActionSubId &&
        arguments.Count == 18;

    private bool TryGetWarehouseExpansionPolicy(
        out WarehouseExpansionPolicySnapshot policy)
    {
        policy = _warehouseExpansionPolicy!;
        try
        {
            policy?.Validate();
            return policy is not null;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine(
                "[warehouse-manager] invalid pinned policy: " +
                exception.Message);
            return false;
        }
    }

    private bool HasCurrentWarehouseRealmAuthority() =>
        _account is not null &&
        _character is not null &&
        _character.AccountId == _account.Id &&
        _character.RealmId == _processRealmId &&
        _processRealmId.IsValid;

    private CommandConnectionCorrelation CreateWarehouseCommandCorrelation(
        WarehouseOperationIdentity identity) =>
        new(
            _commandConnectionId,
            identity.IsSecureClient
                ? CommandTransportKind.SecureTlsLegacy
                : CommandTransportKind.LegacyTcp);

    private static CommandOutcome MapWarehouseExpansionOutcome(
        WarehouseExpansionExecutionDisposition disposition) =>
        disposition switch
        {
            WarehouseExpansionExecutionDisposition.Committed =>
                CommandOutcome.Accepted,
            WarehouseExpansionExecutionDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            WarehouseExpansionExecutionDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            WarehouseExpansionExecutionDisposition.RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            WarehouseExpansionExecutionDisposition.ReplayNotFound =>
                CommandOutcome.ProviderUnavailable,
            _ => CommandOutcome.PreconditionFailed
        };
}
