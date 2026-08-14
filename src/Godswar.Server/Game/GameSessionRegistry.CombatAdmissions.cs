using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly AdmittedCombatRevisionAuthority
        _admittedCombatRevisions = new();

    internal ulong NextAdmittedCombatRevision(
        int accountId,
        int characterId) =>
        _admittedCombatRevisions.Admit(accountId, characterId);

    internal bool TryGetLatestAdmittedCombatRevision(
        int accountId,
        int characterId,
        out ulong revision) =>
        _admittedCombatRevisions.TryGetLatest(
            accountId,
            characterId,
            out revision);
}
