using Godswar.Server.Application.Commands;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

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
        if (_session.IsSecure || _petDurableCommands is not null)
        {
            Console.WriteLine(
                $"[pet] rejected level-up without secure operation " +
                $"identity pet={petId}");
            return;
        }

        CommandMetrics.RecordUnsupportedLegacyIdentity(
            CommandFamily.PetLevelUpgrade);
        PetLevelUpgradeResult result;
        try
        {
            result = await _store.UpgradePetLevelAsync(
                _account.Id,
                _character.Id,
                petId,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[pet] level-up failed pet={petId} character={_character.Name} error={ex.GetType().Name}");
            return;
        }

        if (!result.Succeeded)
        {
            Console.WriteLine(
                $"[pet] level-up rejected pet={petId} character={_character.Name} reason={result.Status}");
            return;
        }

        await _session.SendAsync(
            PacketBuilder.PetLevelUpgrade(
                checked((uint)result.PetId),
                result.Level,
                result.Experience,
                result.BasicSavvy),
            cancellationToken,
            "PetLevelUpgrade");
        Console.WriteLine(
            $"[pet] level-up committed pet={petId} character={_character.Name} level={result.PreviousLevel}->{result.Level} spent={result.ExperienceSpent} remaining={result.Experience}");
    }
}
