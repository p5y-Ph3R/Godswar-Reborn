using Godswar.Server.Application.Characters;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> RefreshCharacterSnapshotAsync(
        string phase,
        CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            _characterLoadSnapshot = null;
            _character = null;
            _characterSnapshotLoaded = false;
            _characterSnapshotBootstrapPending = false;
            return false;
        }

        try
        {
            var accountSnapshot = await _characterSnapshots.ReadAsync(
                _account.Id,
                cancellationToken);
            var hydrated =
                CharacterLoadSnapshotHydrator.Hydrate(accountSnapshot);
            _characterLoadSnapshot = hydrated;
            _character = hydrated?.Character;
            _characterSnapshotLoaded = true;
            _characterSnapshotBootstrapPending = hydrated is not null;
            ResetPlayerMovementEcs();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CharacterSnapshotUnavailableException ex)
        {
            RejectCharacterSnapshot(
                phase,
                CharacterSnapshotMetrics.ReasonCode(ex.Reason));
            return false;
        }
        catch (Exception ex)
        {
            RejectCharacterSnapshot(
                phase,
                $"unexpected_{ex.GetType().Name}");
            return false;
        }
    }

    private void RejectCharacterSnapshot(
        string phase,
        string reason)
    {
        _characterLoadSnapshot = null;
        _character = null;
        _characterSnapshotLoaded = false;
        _characterSnapshotBootstrapPending = false;
        Console.Error.WriteLine(
            $"[character-snapshot] rejected phase={phase} reason={reason}");
        _session.Disconnect();
    }
}
