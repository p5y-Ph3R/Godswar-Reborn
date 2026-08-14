using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleDurablePetToPetMergeAsync(
        PetCommandOperationIdentity identity,
        long primaryPetId,
        long deputyPetId,
        uint materialItemId,
        byte materialQuantity,
        CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(identity, out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.PetToPetMerge,
                identity,
                "provider or active character is unavailable");
            return;
        }

        var correlation = PetCorrelation(identity);
        var command = new PetToPetMergeCommand(
            identity,
            primaryPetId,
            deputyPetId,
            materialItemId,
            materialQuantity);
        var unownedEnvelope = identity.IsSecureClient
            ? PetToPetMergeCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command)
            : PetToPetMergeCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command);
        if (!TryBindCurrentPlayerOwnership(
                unownedEnvelope,
                out var envelope,
                out var ownership))
        {
            return;
        }

        await ExecuteAndCompletePetCommandAsync(
            identity,
            CommandFamily.PetToPetMerge,
            ownership,
            () => _petDurableCommands.ExecuteAsync(
                envelope,
                cancellationToken),
            cancellationToken);
    }
}
