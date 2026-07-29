using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendExperienceBoostStatusAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        ExperienceBoostState boosts;
        try
        {
            boosts = await _registry.GetExperienceBoostStateAsync(
                _session,
                _account.Id,
                _character.Id,
                _character.Camp,
                _character.CurrentMap,
                now,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            boosts = ExperienceBoostState.Empty;
            Console.WriteLine(
                $"[status] EXP boost sync failed character={_character.Name} reason={reason}: {ex.Message}");
        }

        await _registry.RefreshExperienceStatusesAndPublishAsync(
            _session,
            boosts,
            reason,
            cancellationToken);
        Console.WriteLine(
            $"[status] EXP boost sync character={_character.Name} reason={reason} count={boosts.ActiveBoosts.Count} bonus-bps={boosts.TotalBonusBasisPoints}");
    }
}
