namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task RestoreEntryStateAsync(
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        if (!_registry.TryRestoreTrainingDummyEntryState(_character))
        {
            if (_character.CurrentHp <= 0)
            {
                await RestoreFreeRevivalStateAsync(cancellationToken);
                Console.WriteLine(
                    $"[revive] restored dead character during enter " +
                    $"character={_character.Name} " +
                    $"map={_character.CurrentMap} " +
                    $"hp={_character.CurrentHp}/{_character.MaxHp}");
            }
            return;
        }

        _positionDirty = false;
        _lastPositionPersistUtc = DateTime.UtcNow;
        if (!await PersistPositionCheckpointAsync(
                _character,
                force: true,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The training-dummy position checkpoint was not durable.");
        }
        if (!await PersistVitalsCheckpointAsync(
                _character,
                force: true,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The training-dummy revival checkpoint was not durable.");
        }
        Console.WriteLine(
            $"[revive] normalized training dummy entry state " +
            $"character={_character.Name} map={_character.CurrentMap} " +
            $"position={_character.PositionX:F2},{_character.PositionZ:F2} " +
            $"hp={_character.CurrentHp}/{_character.MaxHp} " +
            $"mp={_character.CurrentMp}/{_character.MaxMp}");
    }
}
