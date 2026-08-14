namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private long _legacyAdmittedCombatRevision;

    private long NextAdmittedLegacyCombatRevision()
    {
        var accountId = _account?.Id ?? _character?.AccountId ?? 0;
        var characterId = _character?.Id ?? 0;
        var revision = _registry.NextAdmittedCombatRevision(
            accountId,
            characterId);
        if (revision > long.MaxValue)
        {
            throw new OverflowException(
                "The admitted legacy combat revision was exhausted.");
        }

        var signedRevision = checked((long)revision);
        Volatile.Write(
            ref _legacyAdmittedCombatRevision,
            signedRevision);
        return signedRevision;
    }
}
