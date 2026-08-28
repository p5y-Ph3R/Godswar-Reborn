using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// Admits an authoritative in-memory mutation once, then retains caller
    /// serialization until that accepted command completes. Cancellation or
    /// the ordinary owner invocation timeout cannot abandon accepted work.
    /// </summary>
    private static TResult InvokeWorldOwnerAuthoritativeMutation<TResult>(
        WorldInstanceRuntime runtime,
        Func<MapInstance, TResult> command)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(command);

        var executing = SingleOwnerMailboxExecutionContext.Current;
        if (ReferenceEquals(executing, runtime.Owner))
        {
            return command(runtime.Map);
        }
        if (executing is not null)
        {
            throw new InvalidOperationException(
                "Authoritative cross-mailbox waits are forbidden.");
        }

        var submission = runtime.Owner.TrySubmit(command);
        return submission.RequireCompletion().GetAwaiter().GetResult();
    }

    private static void InvokeWorldOwnerAuthoritativeMutation(
        WorldInstanceRuntime runtime,
        Action<MapInstance> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            map =>
            {
                command(map);
                return SingleOwnerMailboxUnit.Value;
            });
    }
}
