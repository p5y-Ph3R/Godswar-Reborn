namespace Godswar.Server.Application.Sessions;

internal readonly record struct SecureTicketOperationDeadline
{
    private static readonly TimeSpan Minimum =
        TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan Maximum =
        TimeSpan.FromSeconds(30);

    public static SecureTicketOperationDeadline Default { get; } =
        new(TimeSpan.FromMilliseconds(250));

    public SecureTicketOperationDeadline(TimeSpan timeout)
    {
        if (timeout < Minimum || timeout > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "Secure-ticket operation deadlines must be between 1 ms and 30 seconds.");
        }

        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }

    public CancellationTokenSource CreateCancellationSource(
        CancellationToken cancellationToken)
    {
        Validate();
        var lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        lifetime.CancelAfter(Timeout);
        return lifetime;
    }

    public void Validate()
    {
        if (Timeout < Minimum || Timeout > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Timeout),
                "Secure-ticket operation deadlines must be between 1 ms and 30 seconds.");
        }
    }
}
