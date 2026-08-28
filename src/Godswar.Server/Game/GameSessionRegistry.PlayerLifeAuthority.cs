namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private bool HaveEstablishedPlayerLifeAuthoritiesLocked(
        IEnumerable<GameSessionContext> contexts)
    {
        foreach (var context in contexts)
        {
            if (!_sessions.TryGetValue(
                    context.Session,
                    out var current) ||
                !ReferenceEquals(current, context) ||
                !_playerLifeRevisions.ContainsKey(context.Session))
            {
                return false;
            }
        }

        return true;
    }
}
