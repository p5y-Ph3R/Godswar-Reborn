using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal async Task<PreparedPveMonsterKillReward?>
        PrepareClaimedMonsterKillRewardAsync(
            ClientSession sourceSession,
            MonsterDamageResult damageResult)
    {
        ArgumentNullException.ThrowIfNull(sourceSession);
        ArgumentNullException.ThrowIfNull(damageResult);

        Func<MonsterDamageResult,
            Task<PreparedPveMonsterKillReward?>>? prepare = null;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sourceSession, out var source))
            {
                return null;
            }

            var claimantCharacterId =
                damageResult.FirstHitCharacterId ?? source.CharacterId;
            var claimant = _sessions.Values.SingleOrDefault(candidate =>
                candidate.CharacterId == claimantCharacterId &&
                candidate.WorldReady &&
                !candidate.Session.IsDisconnected &&
                candidate.RealmId == source.RealmId &&
                candidate.WorldInstanceId == source.WorldInstanceId &&
                candidate.MapId == source.MapId);
            prepare = claimant?.PreparePveMonsterKillReward;
        }

        return prepare is null
            ? null
            : await prepare(damageResult);
    }
}
