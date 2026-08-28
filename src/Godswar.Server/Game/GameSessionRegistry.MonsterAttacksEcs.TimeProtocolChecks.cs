namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
#if DEBUG
    private Func<DateTimeOffset, DateTimeOffset>?
        _protocolCheckMonsterAttackResolvedAt = null;

    private DateTimeOffset ResolveMonsterAttackTimeForProtocolCheck(
        DateTimeOffset observedAt) =>
        (_protocolCheckMonsterAttackResolvedAt?.Invoke(observedAt) ??
         observedAt).ToUniversalTime();
#endif
}
