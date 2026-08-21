using System.Text.Json;

namespace Godswar.Server;

internal sealed partial class ServerOptions
{
    private void ApplyWorldInstanceEnvironment()
    {
        Game.WorldInstances.RealmId = ReadInt(
            "GODSWAR_WORLD_INSTANCE_REALM_ID",
            Game.WorldInstances.RealmId);

        var routeManifestPath = Environment.GetEnvironmentVariable(
            "GODSWAR_WORLD_INSTANCE_ROUTE_MANIFEST_FILE");
        if (!string.IsNullOrWhiteSpace(routeManifestPath))
        {
            Game.WorldInstances.StaticOpenWorldInstances =
                LoadStaticOpenWorldRoutes(routeManifestPath);
        }

        var serverNodeId = Environment.GetEnvironmentVariable(
            "GODSWAR_WORLD_INSTANCE_SERVER_NODE_ID");
        if (serverNodeId is not null)
        {
            Game.WorldInstances.ServerNodeId = serverNodeId;
        }

        Game.WorldInstances.MaximumRuntimes = ReadInt(
            "GODSWAR_WORLD_INSTANCE_MAXIMUM_RUNTIMES",
            Game.WorldInstances.MaximumRuntimes);
        Game.WorldInstances.MaximumPlayerAssignments = ReadInt(
            "GODSWAR_WORLD_INSTANCE_MAXIMUM_PLAYER_ASSIGNMENTS",
            Game.WorldInstances.MaximumPlayerAssignments);
        Game.WorldInstances.MaximumRetiredInstanceIds = ReadInt(
            "GODSWAR_WORLD_INSTANCE_MAXIMUM_RETIRED_INSTANCE_IDS",
            Game.WorldInstances.MaximumRetiredInstanceIds);
        Game.WorldInstances.DefaultOpenWorldPlayerCapacity = ReadInt(
            "GODSWAR_WORLD_INSTANCE_DEFAULT_OPEN_WORLD_PLAYER_CAPACITY",
            Game.WorldInstances.DefaultOpenWorldPlayerCapacity);
        Game.WorldInstances.MailboxCapacity = ReadInt(
            "GODSWAR_WORLD_INSTANCE_MAILBOX_CAPACITY",
            Game.WorldInstances.MailboxCapacity);
        Game.WorldInstances.OwnerInvocationTimeoutMilliseconds = ReadInt(
            "GODSWAR_WORLD_INSTANCE_OWNER_INVOCATION_TIMEOUT_MILLISECONDS",
            Game.WorldInstances.OwnerInvocationTimeoutMilliseconds);
        Game.WorldInstances.ShutdownDrainTimeoutMilliseconds = ReadInt(
            "GODSWAR_WORLD_INSTANCE_SHUTDOWN_DRAIN_TIMEOUT_MILLISECONDS",
            Game.WorldInstances.ShutdownDrainTimeoutMilliseconds);
        Game.WorldInstances.MaximumFanoutConcurrency = ReadInt(
            "GODSWAR_WORLD_INSTANCE_MAXIMUM_FANOUT_CONCURRENCY",
            Game.WorldInstances.MaximumFanoutConcurrency);
    }

    private static StaticOpenWorldInstanceOptions[]
        LoadStaticOpenWorldRoutes(string path)
    {
        const long maximumManifestBytes = 1024 * 1024;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > maximumManifestBytes)
            {
                throw new InvalidDataException(
                    "The world-instance route manifest must be a nonempty " +
                    $"JSON file no larger than {maximumManifestBytes} bytes.");
            }

            return JsonSerializer.Deserialize<
                    StaticOpenWorldInstanceOptions[]>(
                    File.ReadAllText(file.FullName),
                    JsonDefaults.Indented) ??
                throw new InvalidDataException(
                    "The world-instance route manifest cannot be null.");
        }
        catch (Exception exception)
            when (exception is IOException or
                UnauthorizedAccessException or
                JsonException)
        {
            throw new InvalidDataException(
                "The world-instance route manifest could not be loaded.",
                exception);
        }
    }
}
