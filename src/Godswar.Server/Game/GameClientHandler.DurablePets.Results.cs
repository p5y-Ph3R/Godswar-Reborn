using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendPetLegacyResultAsync(
        PetCommandOperationIdentity identity,
        PetDurableReceipt receipt,
        PetDurableExecutionDisposition executionDisposition,
        CancellationToken cancellationToken)
    {
        var disposition = receipt.Succeeded
            ? executionDisposition switch
            {
                PetDurableExecutionDisposition.Committed =>
                    SecureLegacyCommandDisposition.Applied,
                PetDurableExecutionDisposition.Duplicate =>
                    SecureLegacyCommandDisposition.Replayed,
                _ => SecureLegacyCommandDisposition.Rejected
            }
            : SecureLegacyCommandDisposition.Rejected;
        CommandMetrics.Record(
            receipt.Family,
            identity.Strength,
            executionDisposition switch
            {
                PetDurableExecutionDisposition.Committed when
                    receipt.Succeeded => CommandOutcome.Accepted,
                PetDurableExecutionDisposition.Duplicate =>
                    CommandOutcome.Duplicate,
                _ => CommandOutcome.PreconditionFailed
            });
        if (identity.IsSecureClient)
        {
            await _session.SendLegacyCommandResultAsync(
                new SecureLegacyCommandResult(
                    disposition,
                    (ushort)receipt.Family,
                    ResolvePetLegacyResultCode(receipt),
                    checked((ulong)receipt.AggregateRevision),
                    identity.OperationId),
                cancellationToken);
        }
    }

    internal static uint ResolvePetLegacyResultCode(
        PetDurableReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Family == CommandFamily.PetBind)
        {
            return receipt.Status switch
            {
                PetDurableReceiptStatus.PetBound =>
                    PetManagerProtocol.PetBindSucceededResultSubId,
                PetDurableReceiptStatus.PetAlreadyBound =>
                    PetManagerProtocol.PetBindAlreadyBoundResultSubId,
                PetDurableReceiptStatus.PetBindPetNotSummoned =>
                    PetManagerProtocol.PetBindNoPetResultSubId,
                _ => throw new InvalidDataException(
                    "Pet bind receipt has no native terminal result.")
            };
        }
        if (receipt.Family == CommandFamily.PetAppearanceChange)
        {
            return receipt.Status switch
            {
                PetDurableReceiptStatus.PetAppearanceChanged =>
                    PetManagerProtocol
                        .AppearanceChangeSucceededResultSubId,
                PetDurableReceiptStatus.MagicJadeNotFound =>
                    PetManagerProtocol
                        .AppearanceChangeMissingJadeResultSubId,
                PetDurableReceiptStatus.MagicJadeIncompatible or
                PetDurableReceiptStatus.PetAppearancePetUnavailable =>
                    PetManagerProtocol
                        .AppearanceChangeIncompatibleJadeResultSubId,
                PetDurableReceiptStatus.PetAppearancePetNotSummoned =>
                    PetManagerProtocol.AppearanceChangeNoPetResultSubId,
                PetDurableReceiptStatus.PetAppearancePetUnbound =>
                    PetManagerProtocol
                        .AppearanceChangeUnboundPetResultSubId,
                _ => throw new InvalidDataException(
                    "Pet appearance-change receipt has no native terminal result.")
            };
        }
        if (receipt.Family == CommandFamily.PetBasicSavvyReset)
        {
            return receipt.Status switch
            {
                PetDurableReceiptStatus.PetNotTaken =>
                    PetManagerProtocol.BasicSavvyResetNoPetResultSubId,
                PetDurableReceiptStatus.FairyFeatherNotFound =>
                    PetManagerProtocol.BasicSavvyResetMissingFeatherResultSubId,
                PetDurableReceiptStatus.PetBasicSavvyPreviewed =>
                    PetManagerProtocol
                        .BasicSavvyResetPreviewUnavailableResultSubId,
                PetDurableReceiptStatus.PetBasicSavvyAccepted =>
                    receipt.BasicSavvyPreview is { IsValid: true }
                        ? checked((uint)PetManagerProtocol
                            .BasicSavvyResetSucceededResultSubId)
                        : checked((uint)PetManagerProtocol
                            .BasicSavvyResetPreviewUnavailableResultSubId),
                PetDurableReceiptStatus.PetBasicSavvyPreviewUnavailable =>
                    PetManagerProtocol
                        .BasicSavvyResetPreviewUnavailableResultSubId,
                _ => throw new InvalidDataException(
                    "Pet Basic-Savvy reset receipt has no native terminal result.")
            };
        }
        if (receipt.Family == CommandFamily.PetGrowthReset)
        {
            return receipt.Status switch
            {
                PetDurableReceiptStatus.PetNotTaken =>
                    PetManagerProtocol.GrowthResetNoPetResultSubId,
                PetDurableReceiptStatus.PhoenixFeatherNotFound =>
                    PetManagerProtocol.GrowthResetMissingFeatherResultSubId,
                PetDurableReceiptStatus.PetGrowthReset =>
                    PetManagerProtocol.GrowthResetSucceededResultSubId,
                PetDurableReceiptStatus.PetGrowthPreviewed =>
                    PetManagerProtocol.GrowthResetSucceededResultSubId,
                PetDurableReceiptStatus.PetGrowthAccepted =>
                    PetManagerProtocol.GrowthResetSucceededResultSubId,
                PetDurableReceiptStatus.PetGrowthPreviewUnavailable =>
                    PetManagerProtocol
                        .GrowthResetPreviewUnavailableResultSubId,
                _ => throw new InvalidDataException(
                    "Pet Growth reset receipt has no native terminal result.")
            };
        }
        if (receipt.Family == CommandFamily.PetManagerUtility)
        {
            return ResolvePetManagerUtilityResultCode(receipt);
        }
        if (receipt.Family != CommandFamily.PetSkillUnlearn)
        {
            return (uint)receipt.Status;
        }

        // Family 46 completes a stock Pet Manager modal. Its secure result
        // uses the same terminal sub-ID as opcode 10070, allowing the shim
        // and native UI to settle one operation with one shared result code.
        return receipt.Status switch
        {
            PetDurableReceiptStatus.PetNotTaken =>
                PetManagerProtocol.NoSummonedPetResultSubId,
            PetDurableReceiptStatus.StrongPurgePotionNotFound =>
                PetManagerProtocol.MissingStrongPurgePotionResultSubId,
            PetDurableReceiptStatus.PetSkillNotFound =>
                PetManagerProtocol.EmptySkillSlotResultSubId,
            PetDurableReceiptStatus.PetSkillUnlearned =>
                PetManagerProtocol.SkillUnlearnedResultSubId,
            _ => throw new InvalidDataException(
                "Pet skill-unlearn receipt has no native terminal result.")
        };
    }

    private static uint ResolvePetManagerUtilityResultCode(
        PetDurableReceipt receipt)
    {
        var evidence = receipt.PetManagerUtility ??
            throw new InvalidDataException(
                "Pet Manager utility receipt has no operation evidence.");
        return (evidence.Operation, receipt.Status) switch
        {
            (PetManagerUtilityOperation.CheckGrowth,
                PetDurableReceiptStatus.PetGrowthChecked) =>
                PetManagerProtocol.GrowthCheckTearSpentResultSubId,
            (PetManagerUtilityOperation.CheckGrowth,
                PetDurableReceiptStatus.PetManagerPetNotSummoned) =>
                PetManagerProtocol.NoSummonedPetResultSubId,
            (PetManagerUtilityOperation.CheckGrowth,
                PetDurableReceiptStatus.PetManagerMaterialNotFound) =>
                PetManagerProtocol.GrowthCheckMissingTearResultSubId,

            (PetManagerUtilityOperation.Seal,
                PetDurableReceiptStatus.PetSealed) =>
                PetManagerProtocol.SealSucceededResultSubId,
            (PetManagerUtilityOperation.Seal,
                PetDurableReceiptStatus.PetManagerPetNotSummoned) =>
                PetManagerProtocol.NoSummonedPetResultSubId,
            (PetManagerUtilityOperation.Seal,
                PetDurableReceiptStatus.PetManagerMaterialNotFound) =>
                PetManagerProtocol.SealMissingJadeResultSubId,
            (PetManagerUtilityOperation.Seal,
                PetDurableReceiptStatus.PetManagerBagFull) =>
                PetManagerProtocol.SealBagFullResultSubId,
            (PetManagerUtilityOperation.Seal,
                PetDurableReceiptStatus.PetManagerPetBound) =>
                PetManagerProtocol.SealBoundPetResultSubId,

            (PetManagerUtilityOperation.ClaimPetCall,
                PetDurableReceiptStatus.PetCallClaimed) =>
                PetManagerProtocol.PetCallClaimedResultSubId,
            (PetManagerUtilityOperation.ClaimMerge,
                PetDurableReceiptStatus.PetMergeClaimed) =>
                PetManagerProtocol.MergeClaimedResultSubId,
            (PetManagerUtilityOperation.ClaimPetCall or
                PetManagerUtilityOperation.ClaimMerge,
                PetDurableReceiptStatus.PetManagerBagFull) =>
                PetManagerProtocol.CharmBagFullResultSubId,
            (PetManagerUtilityOperation.ClaimPetCall or
                PetManagerUtilityOperation.ClaimMerge,
                PetDurableReceiptStatus.PetManagerClaimAlreadyHeld) =>
                PetManagerProtocol.CharmAlreadyHeldResultSubId,

            (PetManagerUtilityOperation.ChangeGender,
                PetDurableReceiptStatus.PetGenderChanged) =>
                evidence.NewSex == 1
                    ? checked((uint)PetManagerProtocol
                        .GenderChangedMaleResultSubId)
                    : checked((uint)PetManagerProtocol
                        .GenderChangedFemaleResultSubId),
            (PetManagerUtilityOperation.ChangeGender,
                PetDurableReceiptStatus.PetManagerPetNotSummoned) =>
                PetManagerProtocol.GenderNoPetResultSubId,
            (PetManagerUtilityOperation.ChangeGender,
                PetDurableReceiptStatus.PetManagerMaterialNotFound) =>
                PetManagerProtocol.GenderMissingReverserResultSubId,
            (PetManagerUtilityOperation.ChangeGender,
                PetDurableReceiptStatus.PetManagerGenderUnavailable) =>
                PetManagerProtocol.GenderUnavailableResultSubId,
            (PetManagerUtilityOperation.ChangeGender,
                PetDurableReceiptStatus.PetManagerGenderPetUnbound) =>
                PetManagerProtocol.GenderUnboundPetResultSubId,

            (_, PetDurableReceiptStatus.PetManagerPetUnavailable or
                PetDurableReceiptStatus.PetManagerConcurrentConflict) =>
                evidence.Operation == PetManagerUtilityOperation.ChangeGender
                    ? checked((uint)PetManagerProtocol
                        .GenderUnavailableResultSubId)
                    : checked((uint)PetManagerProtocol
                        .NoSummonedPetResultSubId),
            (PetManagerUtilityOperation.Unseal, _) =>
                checked((uint)receipt.Status),
            _ => throw new InvalidDataException(
                "Pet Manager utility receipt has no native terminal result.")
        };
    }

    internal static PetOperationResultCode
        ResolveAuthoritativePresenceResult(
            PetDurableReceipt receipt,
            bool currentPetExists,
            bool currentIsCarried,
            bool currentIsSummoned)
    {
        if (!receipt.Succeeded)
        {
            return receipt.PresenceOperation switch
            {
                1 => PetOperationResultCode.TakeFailed,
                2 => PetOperationResultCode.CallOutFailed,
                _ => PetOperationResultCode.RecallFailed
            };
        }
        if (currentPetExists &&
            currentIsCarried == receipt.IsCarried &&
            currentIsSummoned == receipt.IsSummoned)
        {
            return HistoricalPresenceResultCode(receipt);
        }
        if (currentPetExists &&
            currentIsCarried &&
            currentIsSummoned)
        {
            return PetOperationResultCode.CallOutSucceeded;
        }

        // A target that is no longer summoned must finish in the recalled
        // presentation even when this packet completes an older CallOut.
        return PetOperationResultCode.RecallSucceeded;
    }

    private static PetOperationResultCode HistoricalPresenceResultCode(
        PetDurableReceipt receipt)
    {
        if (receipt.PresenceOperation == 1)
        {
            return PetOperationResultCode.TakeSucceeded;
        }
        if (receipt.PresenceOperation == 2)
        {
            return PetOperationResultCode.CallOutSucceeded;
        }
        return PetOperationResultCode.RecallSucceeded;
    }

    private bool TryCreatePetSubject(
        PetCommandOperationIdentity identity,
        out CommandSubject subject)
    {
        subject = default;
        if (_account is null ||
            _character is null)
        {
            return false;
        }

        var validTransport = identity.IsSecureClient
            ? _session.IsSecure
            : identity.IsRawLocalServer &&
              !_session.IsSecure &&
              identity.ConnectionId == _commandConnectionId &&
              CanUseLegacyPlayerMutationFallback(
                  _requiresDurablePlayerCommands,
                  isSecureSession: false,
                  _legacyAuthenticationAccess is not null) ||
              identity.IsServerSessionLifecycle &&
              identity.ConnectionId == _commandConnectionId;
        if (!validTransport)
        {
            return false;
        }

        subject = new CommandSubject(_account.Id, _character.Id);
        return true;
    }

    private CommandConnectionCorrelation PetCorrelation(
        PetCommandOperationIdentity identity) =>
        new(
            _commandConnectionId,
            identity.IsSecureClient ||
            identity.IsServerSessionLifecycle && _session.IsSecure
                ? CommandTransportKind.SecureTlsLegacy
                : CommandTransportKind.LegacyTcp);

    private void RecordPetProviderUnavailable(
        CommandFamily family,
        PetCommandOperationIdentity identity,
        string reason)
    {
        CommandMetrics.Record(
            family,
            identity.Strength,
            CommandOutcome.ProviderUnavailable);
        Console.Error.WriteLine(
            "[pet] durable command unresolved; operation remains " +
            $"pending family={(ushort)family} " +
            $"operation={identity.OperationId}: " +
            reason);
    }
}
