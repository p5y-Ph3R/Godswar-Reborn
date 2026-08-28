using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private IMedusaCompletionRewardStore? _medusaCompletionRewards;

    internal void ConfigureMedusaCompletionRewards(
        IMedusaCompletionRewardStore? store)
    {
        if (store is null)
        {
            return;
        }

        var current = Interlocked.CompareExchange(
            ref _medusaCompletionRewards,
            store,
            null);
        if (current is not null && !ReferenceEquals(current, store))
        {
            throw new InvalidOperationException(
                "Medusa completion rewards are already configured.");
        }
    }
}
