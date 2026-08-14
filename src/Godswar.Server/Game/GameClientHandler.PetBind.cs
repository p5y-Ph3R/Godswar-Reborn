using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<PetDurableReceipt?> HandleDurablePetBindAsync(
        PetCommandOperationIdentity identity,
        CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(identity, out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.PetBind,
                identity,
                "provider or active character is unavailable");
            return null;
        }

        var correlation = PetCorrelation(identity);
        var command = new PetBindCommand(identity);
        var unownedEnvelope = identity.IsSecureClient
            ? PetBindCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command)
            : PetBindCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command);
        if (!TryBindCurrentPlayerOwnership(
                unownedEnvelope,
                out var envelope,
                out var ownership))
        {
            return null;
        }

        return await ExecuteAndCompletePetCommandAsync(
            identity,
            CommandFamily.PetBind,
            ownership,
            () => _petDurableCommands.ExecuteAsync(
                envelope,
                cancellationToken),
            cancellationToken);
    }

    private async Task<bool> SendPetBindProjectionAsync(
        PetDurableReceipt receipt,
        PetDurableExecutionDisposition disposition,
        IReadOnlyList<PetBootstrapSnapshot> pets,
        CancellationToken cancellationToken)
    {
        if (receipt.Status ==
            PetDurableReceiptStatus.PetBindPetNotSummoned)
        {
            return true;
        }

        var current = pets.SingleOrDefault(
            pet => pet.PetId == receipt.PetId);
        if (disposition == PetDurableExecutionDisposition.Committed &&
            (current is null || !current.IsBound ||
             current.Revision != receipt.PetRevision))
        {
            return false;
        }
        if (current is null)
        {
            return true;
        }

        var cyclesSummonedModel =
            receipt.Succeeded &&
            current.IsCarried && current.IsSummoned &&
            !current.ContributesToCharacter;
        if (cyclesSummonedModel)
        {
            await _session.SendAsync(
                PacketBuilder.PetOperationResult(
                    checked((uint)current.PetId),
                    PetOperationResultCode.RecallSucceeded),
                cancellationToken,
                "DurablePetBindModelRecall");
        }

        await _session.SendAsync(
            PacketBuilder.PetAppearanceRefresh(
                RequirePetContent(),
                current),
            cancellationToken,
            "DurablePetBindRefresh");

        if (cyclesSummonedModel)
        {
            await _session.SendAsync(
                PacketBuilder.PetOperationResult(
                    checked((uint)current.PetId),
                    PetOperationResultCode.CallOutSucceeded),
                cancellationToken,
                "DurablePetBindCallOutRefresh");
            await _session.SendAsync(
                PacketBuilder.PetWorldPresence(
                    checked((uint)current.PetId),
                    LocalPlayerObjectId),
                cancellationToken,
                "DurablePetBindWorldRefresh");
        }
        return true;
    }
}
