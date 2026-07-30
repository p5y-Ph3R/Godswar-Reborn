using Godswar.Server.Application.Characters;

namespace Godswar.Server.Operations;

internal static class CriticalTaskShutdown
{
    public static async Task<bool> CompleteAsync(
        CharacterCheckpointCoordinator checkpoints,
        CancellationTokenSource shutdown,
        IEnumerable<Task> tasks,
        TimeSpan completionTimeout)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(shutdown);
        ArgumentNullException.ThrowIfNull(tasks);
        if (completionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completionTimeout));
        }

        checkpoints.Complete();
        TryCancel(shutdown);
        try
        {
            await Task.WhenAll(tasks).WaitAsync(completionTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            checkpoints.ForceStop();
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void TryCancel(CancellationTokenSource shutdown)
    {
        try
        {
            shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
