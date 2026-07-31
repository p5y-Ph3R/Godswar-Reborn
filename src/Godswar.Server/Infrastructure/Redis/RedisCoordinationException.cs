using Godswar.Server.Application.Coordination;

namespace Godswar.Server.Infrastructure.Redis;

internal sealed class RedisCoordinationException :
    InvalidOperationException
{
    public RedisCoordinationException(
        CoordinationOperationStatus status,
        Exception? innerException = null)
        : base(MessageFor(status), innerException)
    {
        if (status is not (
                CoordinationOperationStatus.Unavailable or
                CoordinationOperationStatus.Overloaded or
                CoordinationOperationStatus.CircuitOpen or
                CoordinationOperationStatus.DeadlineExceeded))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
    }

    public CoordinationOperationStatus Status { get; }

    private static string MessageFor(CoordinationOperationStatus status) =>
        status switch
        {
            CoordinationOperationStatus.Unavailable =>
                "Redis coordination is unavailable.",
            CoordinationOperationStatus.Overloaded =>
                "Redis coordination admission is full.",
            CoordinationOperationStatus.CircuitOpen =>
                "Redis coordination circuit is open.",
            CoordinationOperationStatus.DeadlineExceeded =>
                "Redis coordination deadline elapsed.",
            _ => "Redis coordination failed."
        };
}
