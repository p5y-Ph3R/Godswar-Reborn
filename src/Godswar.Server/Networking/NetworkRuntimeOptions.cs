using System.Text.Json.Serialization;

namespace Godswar.Server.Networking;

internal sealed class NetworkRuntimeOptions
{
    public int ListenBacklog { get; set; } = 512;

    public int MaxActiveConnections { get; set; } = 512;

    public int MaxConcurrentTlsHandshakes { get; set; } = 64;

    public int MaxUnauthenticatedConnections { get; set; } = 128;

    public int MaxUnauthenticatedConnectionsPerIp { get; set; } = 4;

    public int MaxUnauthenticatedConnectionsPerPrefix { get; set; } = 32;

    public int IngressQueueItems { get; set; } = 128;

    public int IngressQueueBytes { get; set; } = 512 * 1024;

    public int ReliableEgressQueueItems { get; set; } = 128;

    public int ReliableEgressQueueBytes { get; set; } = 512 * 1024;

    public int ReliableEgressPendingItems { get; set; } = 512;

    public int ReliableEgressPendingBytes { get; set; } = 2 * 1024 * 1024;

    public int ControlQueueItems { get; set; } = 32;

    public int ControlQueueBytes { get; set; } = 64 * 1024;

    public int QueueAdmissionTimeoutMilliseconds { get; set; } = 2_000;

    public int FirstPacketTimeoutMilliseconds { get; set; } = 10_000;

    public int PacketHeaderTimeoutMilliseconds { get; set; } = 5_000;

    public int PacketBodyTimeoutMilliseconds { get; set; } = 10_000;

    public int ReliableWriteTimeoutMilliseconds { get; set; } = 5_000;

    public int IdleTimeoutMilliseconds { get; set; } = 90_000;

    public int GracefulDrainTimeoutMilliseconds { get; set; } = 5_000;

    [JsonIgnore]
    public TimeSpan QueueAdmissionTimeout =>
        TimeSpan.FromMilliseconds(QueueAdmissionTimeoutMilliseconds);

    [JsonIgnore]
    public TimeSpan FirstPacketTimeout =>
        TimeSpan.FromMilliseconds(FirstPacketTimeoutMilliseconds);

    [JsonIgnore]
    public TimeSpan PacketHeaderTimeout =>
        TimeSpan.FromMilliseconds(PacketHeaderTimeoutMilliseconds);

    [JsonIgnore]
    public TimeSpan PacketBodyTimeout =>
        TimeSpan.FromMilliseconds(PacketBodyTimeoutMilliseconds);

    [JsonIgnore]
    public TimeSpan ReliableWriteTimeout =>
        TimeSpan.FromMilliseconds(ReliableWriteTimeoutMilliseconds);

    [JsonIgnore]
    public TimeSpan IdleTimeout =>
        TimeSpan.FromMilliseconds(IdleTimeoutMilliseconds);

    [JsonIgnore]
    public TimeSpan GracefulDrainTimeout =>
        TimeSpan.FromMilliseconds(GracefulDrainTimeoutMilliseconds);

    public void Validate()
    {
        RequireRange(ListenBacklog, 1, 65_535, nameof(ListenBacklog));
        RequireRange(
            MaxActiveConnections,
            1,
            100_000,
            nameof(MaxActiveConnections));
        RequireRange(
            MaxUnauthenticatedConnections,
            1,
            MaxActiveConnections,
            nameof(MaxUnauthenticatedConnections));
        RequireRange(
            MaxConcurrentTlsHandshakes,
            1,
            MaxUnauthenticatedConnections,
            nameof(MaxConcurrentTlsHandshakes));
        RequireRange(
            MaxUnauthenticatedConnectionsPerIp,
            1,
            MaxUnauthenticatedConnections,
            nameof(MaxUnauthenticatedConnectionsPerIp));
        RequireRange(
            MaxUnauthenticatedConnectionsPerPrefix,
            MaxUnauthenticatedConnectionsPerIp,
            MaxUnauthenticatedConnections,
            nameof(MaxUnauthenticatedConnectionsPerPrefix));

        ValidateQueue(
            IngressQueueItems,
            IngressQueueBytes,
            nameof(IngressQueueItems),
            nameof(IngressQueueBytes),
            LegacyProtocolLimits.MaxPacketLength);
        ValidateQueue(
            ReliableEgressQueueItems,
            ReliableEgressQueueBytes,
            nameof(ReliableEgressQueueItems),
            nameof(ReliableEgressQueueBytes),
            LegacyProtocolLimits.MaxPacketLength);
        ValidateQueue(
            ReliableEgressPendingItems,
            ReliableEgressPendingBytes,
            nameof(ReliableEgressPendingItems),
            nameof(ReliableEgressPendingBytes),
            ReliableEgressQueueBytes);
        ValidateQueue(
            ControlQueueItems,
            ControlQueueBytes,
            nameof(ControlQueueItems),
            nameof(ControlQueueBytes));

        RequireTimeout(
            QueueAdmissionTimeoutMilliseconds,
            nameof(QueueAdmissionTimeoutMilliseconds));
        RequireTimeout(
            FirstPacketTimeoutMilliseconds,
            nameof(FirstPacketTimeoutMilliseconds));
        RequireTimeout(
            PacketHeaderTimeoutMilliseconds,
            nameof(PacketHeaderTimeoutMilliseconds));
        RequireTimeout(
            PacketBodyTimeoutMilliseconds,
            nameof(PacketBodyTimeoutMilliseconds));
        RequireTimeout(
            ReliableWriteTimeoutMilliseconds,
            nameof(ReliableWriteTimeoutMilliseconds));
        RequireTimeout(
            IdleTimeoutMilliseconds,
            nameof(IdleTimeoutMilliseconds));
        RequireTimeout(
            GracefulDrainTimeoutMilliseconds,
            nameof(GracefulDrainTimeoutMilliseconds));
    }

    private static void ValidateQueue(
        int itemLimit,
        int byteLimit,
        string itemName,
        string byteName,
        int minimumByteLimit = 1)
    {
        RequireRange(itemLimit, 1, 4_096, itemName);
        RequireRange(
            byteLimit,
            Math.Max(itemLimit, minimumByteLimit),
            64 * 1024 * 1024,
            byteName);
    }

    private static void RequireTimeout(int milliseconds, string name)
    {
        RequireRange(milliseconds, 1, 10 * 60 * 1_000, name);
    }

    private static void RequireRange(
        int value,
        int minimum,
        int maximum,
        string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"{name} must be between {minimum} and {maximum}, but was {value}.");
        }
    }
}
