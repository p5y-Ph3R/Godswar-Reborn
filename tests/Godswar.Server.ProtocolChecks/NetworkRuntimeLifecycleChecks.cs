namespace Godswar.Server.ProtocolChecks;

internal static class NetworkRuntimeLifecycleChecks
{
    public static async Task RunAsync()
    {
        CheckRuntimeOptions();
        await ConnectionAdmissionChecks.RunAsync();
        await BoundedByteQueueChecks.RunAsync();
        await ClientSessionRuntimeChecks.RunAsync();
        await NetworkRuntimeMetricsChecks.RunAsync();
        await TcpEndpointServerLifecycleChecks.RunAsync();
    }

    private static void CheckRuntimeOptions()
    {
        var defaults = new Godswar.Server.Networking.NetworkRuntimeOptions();
        defaults.Validate();
        Check.Equal(512, defaults.ListenBacklog, "network listen backlog default");
        Check.Equal(512, defaults.MaxActiveConnections, "network active limit default");
        Check.Equal(128, defaults.MaxUnauthenticatedConnections, "network unauthenticated limit default");
        Check.Equal(128, defaults.ReliableEgressQueueItems, "reliable egress item default");
        Check.Equal(512 * 1024, defaults.ReliableEgressQueueBytes, "reliable egress byte default");
        Check.Equal(512, defaults.ReliableEgressPendingItems, "pending egress item default");
        Check.Equal(2 * 1024 * 1024, defaults.ReliableEgressPendingBytes, "pending egress byte default");
        Check.Equal(2_000, defaults.QueueAdmissionTimeoutMilliseconds, "queue deadline default");
        Check.Equal(5_000, defaults.GracefulDrainTimeoutMilliseconds, "drain deadline default");

        Check.Throws<InvalidDataException>(
            () => (new Godswar.Server.Networking.NetworkRuntimeOptions
            {
                MaxActiveConnections = 1,
                MaxUnauthenticatedConnections = 2
            }).Validate(),
            "inconsistent connection limits fail startup validation");
        Check.Throws<InvalidDataException>(
            () => (new Godswar.Server.Networking.NetworkRuntimeOptions
            {
                ReliableEgressQueueItems = 0
            }).Validate(),
            "zero queue capacity fails startup validation");
        Check.Throws<InvalidDataException>(
            () => (new Godswar.Server.Networking.NetworkRuntimeOptions
            {
                ReliableEgressQueueBytes =
                    Godswar.Server.Networking.LegacyProtocolLimits.MaxPacketLength - 1
            }).Validate(),
            "reliable capacity below one maximum legacy packet fails validation");
        Check.Throws<InvalidDataException>(
            () => (new Godswar.Server.Networking.NetworkRuntimeOptions
            {
                PacketBodyTimeoutMilliseconds = 0
            }).Validate(),
            "zero network deadline fails startup validation");
    }
}
