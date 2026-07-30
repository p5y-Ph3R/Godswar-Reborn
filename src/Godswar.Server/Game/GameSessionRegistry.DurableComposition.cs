namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly bool _requiresDurablePlayerPersistence;

    private void ValidateDurablePlayerPersistenceComposition()
    {
        if (!_requiresDurablePlayerPersistence)
        {
            return;
        }

        if (_checkpointCoordinator is null ||
            _progressionIntervalSettlementCommands is null)
        {
            throw new InvalidOperationException(
                "PostgreSQL player persistence requires both the character " +
                "checkpoint coordinator and progression interval executor.");
        }
    }

    private void RequireLegacyRegistryMutationAllowed(string operation)
    {
        if (!_requiresDurablePlayerPersistence)
        {
            return;
        }

        throw new InvalidOperationException(
            "PostgreSQL player persistence cannot use a broad legacy store " +
            $"mutation: {operation}.");
    }
}
