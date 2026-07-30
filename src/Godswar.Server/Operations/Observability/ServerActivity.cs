using System.Diagnostics;

namespace Godswar.Server.Operations.Observability;

internal static class ServerActivity
{
    private const ulong PacketSampleMask = 63;

    public const string SourceName = "Godswar.Server";

    public static readonly TimeSpan MaximumRetrospectiveDuration =
        TimeSpan.FromHours(1);

    private static readonly ActivitySource Source = new(SourceName);
    private static long _packetSequence;

    public static ServerPacketTraceScope StartPacket(
        ServerTraceOperation operation,
        string endpoint,
        string transport)
    {
        if (operation is not ServerTraceOperation.LoginPacket and not
            ServerTraceOperation.GamePacket)
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        endpoint = SafeTelemetryCode.Require(
            endpoint,
            nameof(endpoint));
        transport = SafeTelemetryCode.Require(
            transport,
            nameof(transport));
        var started = Stopwatch.GetTimestamp();
        var sequence = unchecked(
            (ulong)Interlocked.Increment(ref _packetSequence));
        var activity = (sequence & PacketSampleMask) == 0
            ? Start(
                operation,
                ActivityKind.Server,
                ServerTraceAttribute.FromCode(
                    ServerTraceTag.Endpoint,
                    endpoint),
                ServerTraceAttribute.FromCode(
                    ServerTraceTag.Transport,
                    transport))
            : null;
        return new ServerPacketTraceScope(
            operation,
            endpoint,
            transport,
            started,
            activity);
    }

    public static Activity? Start(
        ServerTraceOperation operation,
        ActivityKind kind = ActivityKind.Internal,
        params ReadOnlySpan<ServerTraceAttribute> attributes)
    {
        Validate(operation, attributes);
        var activity = Source.StartActivity(
            ServerTraceCodes.Operation(operation),
            kind);
        SetAttributes(activity, attributes);
        return activity;
    }

    public static Activity? StartAt(
        ServerTraceOperation operation,
        DateTimeOffset startedAtUtc,
        ActivityKind kind = ActivityKind.Internal,
        params ReadOnlySpan<ServerTraceAttribute> attributes)
    {
        Validate(operation, attributes);
        var now = TimeProvider.System.GetUtcNow();
        if (!IsBoundedPastTimestamp(startedAtUtc, now))
        {
            return null;
        }

        var parent = Activity.Current?.Context ?? default;
        var activity = Source.StartActivity(
            ServerTraceCodes.Operation(operation),
            kind,
            parent,
            tags: null,
            links: null,
            startedAtUtc.ToUniversalTime());
        SetAttributes(activity, attributes);
        return activity;
    }

    public static bool RecordCompleted(
        ServerTraceOperation operation,
        TimeSpan elapsed,
        ServerTraceOutcome outcome,
        ActivityKind kind = ActivityKind.Internal,
        params ReadOnlySpan<ServerTraceAttribute> attributes)
    {
        Validate(operation, attributes);
        if (elapsed < TimeSpan.Zero ||
            elapsed > MaximumRetrospectiveDuration)
        {
            return false;
        }

        var completedAtUtc = TimeProvider.System.GetUtcNow();
        return RecordCompletedCore(
            operation,
            completedAtUtc - elapsed,
            completedAtUtc,
            outcome,
            kind,
            attributes);
    }

    public static bool RecordCompleted(
        ServerTraceOperation operation,
        DateTimeOffset startedAtUtc,
        ServerTraceOutcome outcome,
        ActivityKind kind = ActivityKind.Internal,
        params ReadOnlySpan<ServerTraceAttribute> attributes)
    {
        Validate(operation, attributes);
        var completedAtUtc = TimeProvider.System.GetUtcNow();
        if (!IsBoundedPastTimestamp(startedAtUtc, completedAtUtc))
        {
            return false;
        }

        return RecordCompletedCore(
            operation,
            startedAtUtc.ToUniversalTime(),
            completedAtUtc,
            outcome,
            kind,
            attributes);
    }

    public static void Complete(
        Activity? activity,
        ServerTraceOutcome outcome)
    {
        if (activity is null)
        {
            return;
        }
        var code = ServerTraceCodes.Outcome(outcome);
        activity.SetTag(
            ServerTraceCodes.Tag(ServerTraceTag.Outcome),
            code);
        activity.SetStatus(
            outcome is ServerTraceOutcome.Faulted or
                ServerTraceOutcome.Rejected
                ? ActivityStatusCode.Error
                : ActivityStatusCode.Ok);
    }

    private static bool RecordCompletedCore(
        ServerTraceOperation operation,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        ServerTraceOutcome outcome,
        ActivityKind kind,
        ReadOnlySpan<ServerTraceAttribute> attributes)
    {
        var parent = Activity.Current?.Context ?? default;
        using var activity = Source.StartActivity(
            ServerTraceCodes.Operation(operation),
            kind,
            parent,
            tags: null,
            links: null,
            startedAtUtc);
        if (activity is null)
        {
            return false;
        }

        SetAttributes(activity, attributes);
        Complete(activity, outcome);
        activity.SetEndTime(completedAtUtc.UtcDateTime);
        return true;
    }

    private static bool IsBoundedPastTimestamp(
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        var elapsed =
            completedAtUtc - startedAtUtc.ToUniversalTime();
        return elapsed >= TimeSpan.Zero &&
            elapsed <= MaximumRetrospectiveDuration;
    }

    private static void SetAttributes(
        Activity? activity,
        ReadOnlySpan<ServerTraceAttribute> attributes)
    {
        if (activity is null)
        {
            return;
        }

        foreach (var attribute in attributes)
        {
            var name = ServerTraceCodes.Tag(attribute.Tag);
            switch (attribute.Kind)
            {
                case ServerTraceValueKind.Code:
                    activity.SetTag(name, attribute.Code);
                    break;
                case ServerTraceValueKind.Number:
                    activity.SetTag(name, attribute.Number);
                    break;
                case ServerTraceValueKind.Boolean:
                    activity.SetTag(name, attribute.Boolean);
                    break;
                default:
                    activity.Dispose();
                    throw new ArgumentOutOfRangeException(
                        nameof(attributes),
                        "Unknown server trace value kind.");
            }
        }
    }

    private static void Validate(
        ServerTraceOperation operation,
        ReadOnlySpan<ServerTraceAttribute> attributes)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }
        if (attributes.Length > StructuredLogOptions.MaximumFields)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attributes),
                "A server activity has a fixed attribute bound.");
        }

        ValidateUniqueAttributes(attributes);
    }

    private static void ValidateUniqueAttributes(
        ReadOnlySpan<ServerTraceAttribute> attributes)
    {
        Span<bool> seen = stackalloc bool[11];
        foreach (var attribute in attributes)
        {
            var index = (int)attribute.Tag;
            if (index <= 0 ||
                index >= seen.Length ||
                seen[index])
            {
                throw new ArgumentException(
                    "Server trace attributes must be finite and unique.",
                    nameof(attributes));
            }
            seen[index] = true;
        }
    }
}

internal readonly struct ServerPacketTraceScope : IDisposable
{
    private readonly Activity? _activity;
    private readonly string _endpoint;
    private readonly ServerTraceOperation _operation;
    private readonly long _started;
    private readonly string _transport;

    internal ServerPacketTraceScope(
        ServerTraceOperation operation,
        string endpoint,
        string transport,
        long started,
        Activity? activity)
    {
        _operation = operation;
        _endpoint = endpoint;
        _transport = transport;
        _started = started;
        _activity = activity;
    }

    public void Complete(ServerTraceOutcome outcome)
    {
        if (_activity is not null)
        {
            ServerActivity.Complete(_activity, outcome);
            return;
        }
        if (outcome is not ServerTraceOutcome.Faulted and not
            ServerTraceOutcome.Rejected)
        {
            return;
        }

        ServerActivity.RecordCompleted(
            _operation,
            Stopwatch.GetElapsedTime(_started),
            outcome,
            ActivityKind.Server,
            ServerTraceAttribute.FromCode(
                ServerTraceTag.Endpoint,
                _endpoint),
            ServerTraceAttribute.FromCode(
                ServerTraceTag.Transport,
                _transport));
    }

    public void Dispose() => _activity?.Dispose();
}
