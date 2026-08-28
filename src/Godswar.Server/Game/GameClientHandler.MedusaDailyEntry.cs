using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const ushort NonDurableMedusaDailyEntryLimit = 1;

    private async Task<IReadOnlySet<int>?>
        TryFindUsedMedusaDailyEntryCharactersAsync(
            RealmId realmId,
            DateOnly realmDay,
            IReadOnlyCollection<int> characterIds,
            CancellationToken cancellationToken)
    {
        if (_medusaDailyEntries is null)
        {
            return _registry.FindUsedLocalMedusaDailyEntryCharacters(
                realmId,
                realmDay,
                characterIds);
        }

        try
        {
            return await _medusaDailyEntries.FindUsedCharacterIdsAsync(
                realmId,
                realmDay,
                characterIds,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "[instance-caller] Medusa daily-entry lookup failed: " +
                error.Message);
            return null;
        }
    }

    private async Task<ushort?> TryClaimMedusaDailyEntryAsync(
        Guid reservationId,
        RealmId realmId,
        DateOnly realmDay,
        MedusaEncounterDifficulty difficulty,
        IReadOnlyCollection<int> characterIds,
        DateTimeOffset claimedAtUtc,
        CancellationToken cancellationToken)
    {
        if (_medusaDailyEntries is null)
        {
            return _registry.TryReserveLocalMedusaDailyEntry(
                reservationId,
                realmId,
                realmDay,
                characterIds)
                    ? NonDurableMedusaDailyEntryLimit
                    : null;
        }

        try
        {
            var result = await _medusaDailyEntries.TryClaimAsync(
                new MedusaDailyEntryClaimRequest(
                    reservationId,
                    realmId,
                    realmDay,
                    difficulty,
                    characterIds,
                    claimedAtUtc.ToUniversalTime()),
                cancellationToken);
            return result.Status == MedusaDailyEntryClaimStatus.Claimed
                ? result.DailyEntryLimit
                : null;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "[instance-caller] Medusa daily-entry claim failed: " +
                error.Message);
            return null;
        }
    }

    private async Task ReleaseMedusaDailyEntryAsync(Guid reservationId)
    {
        if (_medusaDailyEntries is null)
        {
            _registry.ReleaseLocalMedusaDailyEntry(reservationId);
            return;
        }

        try
        {
            await _medusaDailyEntries.ReleaseAsync(
                reservationId,
                CancellationToken.None);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "[instance-caller] Medusa daily-entry release failed: " +
                error.Message);
        }
    }
}
