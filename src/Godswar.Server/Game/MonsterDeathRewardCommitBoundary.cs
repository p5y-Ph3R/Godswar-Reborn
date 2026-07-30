namespace Godswar.Server.Game;

internal static class MonsterDeathRewardCommitBoundary
{
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> commit,
        bool allowImmediateReplay,
        Action<Exception>? onImmediateReplay = null)
    {
        ArgumentNullException.ThrowIfNull(commit);
        try
        {
            return await commit(CancellationToken.None);
        }
        catch (Exception firstFailure) when (allowImmediateReplay)
        {
            onImmediateReplay?.Invoke(firstFailure);
            return await commit(CancellationToken.None);
        }
    }
}
