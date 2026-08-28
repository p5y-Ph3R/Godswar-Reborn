namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private Task<PreparedPveMonsterKillReward?>
        PrepareClaimedMonsterKillRewardAsync(
            MonsterDamageResult damageResult) =>
        _registry.PrepareClaimedMonsterKillRewardAsync(
            _session,
            damageResult);
}
