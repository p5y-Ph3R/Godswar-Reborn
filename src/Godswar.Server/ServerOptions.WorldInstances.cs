namespace Godswar.Server;

internal sealed partial class ServerOptions
{
    private void ApplyWorldInstanceEnvironment()
    {
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
}
