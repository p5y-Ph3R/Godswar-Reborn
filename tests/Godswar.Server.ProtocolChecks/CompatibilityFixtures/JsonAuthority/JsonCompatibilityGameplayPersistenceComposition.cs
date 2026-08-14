using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks.CompatibilityFixtures.JsonAuthority;

/// <summary>
/// Test-only composition for exercising the retired JSON authority fixture.
/// Production composition is deliberately PostgreSQL-only.
/// </summary>
internal static class JsonCompatibilityGameplayPersistenceComposition
{
    public static ServerGameplayPersistenceProviders Create(
        JsonGameStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new ServerGameplayPersistenceProviders(
            store,
            store,
            store,
            store,
            store,
            store,
            store,
            store,
            store);
    }
}
