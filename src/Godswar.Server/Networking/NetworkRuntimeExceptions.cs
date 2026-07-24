namespace Godswar.Server.Networking;

internal sealed class NetworkDeadlineException : IOException
{
    public NetworkDeadlineException(NetworkTimeoutStage stage)
        : base($"Network deadline exceeded at stage '{stage.ToMetricTag()}'.")
    {
        Stage = stage;
    }

    public NetworkTimeoutStage Stage { get; }
}

internal sealed class ReliableQueueOverflowException : IOException
{
    public ReliableQueueOverflowException()
        : base("Reliable network queue admission deadline exceeded.")
    {
    }
}
