using Godswar.Server.Application.Commands;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandlePetLevelUpgradeAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (!TryReadPetId(packet, out var petId))
        {
            Console.WriteLine(
                $"[pet] rejected malformed level-up character={_character?.Name ?? "<none>"} length={packet.Length}");
            return;
        }

        if (_account is null || _character is null)
        {
            Console.WriteLine(
                $"[pet] rejected unauthenticated level-up pet={petId}");
            return;
        }

        if (packet.ClientOperationId is { } operationId)
        {
            await HandleDurablePetLevelUpgradeAsync(
                operationId,
                petId,
                cancellationToken);
            return;
        }
        CommandMetrics.RecordUnsupportedLegacyIdentity(
            CommandFamily.PetLevelUpgrade);
        Console.WriteLine(
            $"[pet] rejected level-up without durable operation " +
            $"identity pet={petId}");
    }
}
