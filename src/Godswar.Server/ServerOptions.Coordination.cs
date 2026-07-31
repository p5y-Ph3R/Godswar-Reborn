namespace Godswar.Server;

internal sealed partial class ServerOptions
{
    public CoordinationRuntimeOptions Coordination { get; set; } = new();

    private void ValidateCoordinationTopology()
    {
        if (Coordination.ProviderKind !=
            CoordinationProviderKind.Redis)
        {
            return;
        }

        if (!string.Equals(
                Storage.Provider,
                nameof(GameStorageProviderKind.Postgres),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Redis coordination requires PostgreSQL durable ownership " +
                "fences; JSON storage cannot enable it.");
        }
        if (string.Equals(
                RuntimeProfile,
                nameof(ServerRuntimeProfileKind.Production),
                StringComparison.OrdinalIgnoreCase) &&
            !Coordination.RequireTls)
        {
            throw new InvalidDataException(
                "Production Redis coordination requires TLS.");
        }
        if (Coordination.Capacity < Math.Max(
                Game.WorldInstances.DefaultOpenWorldPlayerCapacity,
                Network.MaxActiveConnections) ||
            Secure.Enabled &&
            Coordination.Capacity < Secure.Tickets.Capacity)
        {
            throw new InvalidDataException(
                "Coordination capacity must cover configured connections, " +
                "ticket capacity, and one open-world capacity.");
        }
    }
}
