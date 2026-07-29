namespace Godswar.Server.Application.Commands;

internal enum CommandAttemptDecision : byte
{
    Accepted = 1,
    DuplicatePending = 2,
    DuplicateCompleted = 3,
    RequestHashConflict = 4
}

/// <summary>
/// Bounded, process-local correlation for command attempts. This is not an
/// authoritative inbox and never substitutes for the B08 PostgreSQL record.
/// </summary>
internal sealed class BoundedCommandAttemptRegistry
{
    public const int DefaultCapacity = 8_192;
    public static readonly TimeSpan DefaultRetention =
        TimeSpan.FromMinutes(15);

    private readonly int _capacity;
    private readonly TimeSpan _retention;
    private readonly object _gate = new();
    private readonly Dictionary<
        string,
        LinkedListNode<Attempt>> _byOperation =
            new(StringComparer.Ordinal);
    private readonly LinkedList<Attempt> _ordered = new();

    public BoundedCommandAttemptRegistry(
        int capacity = DefaultCapacity,
        TimeSpan? retention = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        var effectiveRetention = retention ?? DefaultRetention;
        if (effectiveRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention));
        }

        _capacity = capacity;
        _retention = effectiveRetention;
    }

    public CommandAttemptDecision TryBegin(
        string operationId,
        string requestHash,
        DateTimeOffset now)
    {
        ValidateDigest(operationId, nameof(operationId));
        ValidateDigest(requestHash, nameof(requestHash));

        lock (_gate)
        {
            RemoveExpired(now);
            if (_byOperation.TryGetValue(
                    operationId,
                    out var existing))
            {
                if (!string.Equals(
                        existing.Value.RequestHash,
                        requestHash,
                        StringComparison.Ordinal))
                {
                    return CommandAttemptDecision.RequestHashConflict;
                }

                return existing.Value.Completed
                    ? CommandAttemptDecision.DuplicateCompleted
                    : CommandAttemptDecision.DuplicatePending;
            }

            while (_byOperation.Count >= _capacity)
            {
                RemoveOldest();
            }

            var attempt = new Attempt(
                operationId,
                requestHash,
                now + _retention,
                Completed: false);
            var node = _ordered.AddLast(attempt);
            _byOperation.Add(operationId, node);
            return CommandAttemptDecision.Accepted;
        }
    }

    public void Complete(
        string operationId,
        string requestHash,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            RemoveExpired(now);
            if (!_byOperation.TryGetValue(
                    operationId,
                    out var node) ||
                !string.Equals(
                    node.Value.RequestHash,
                    requestHash,
                    StringComparison.Ordinal))
            {
                return;
            }

            node.Value = node.Value with
            {
                Completed = true,
                ExpiresAt = now + _retention
            };
            _ordered.Remove(node);
            _ordered.AddLast(node);
        }
    }

    public void Release(
        string operationId,
        string requestHash)
    {
        lock (_gate)
        {
            if (!_byOperation.TryGetValue(
                    operationId,
                    out var node) ||
                node.Value.Completed ||
                !string.Equals(
                    node.Value.RequestHash,
                    requestHash,
                    StringComparison.Ordinal))
            {
                return;
            }

            _byOperation.Remove(operationId);
            _ordered.Remove(node);
        }
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _byOperation.Count;
            }
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        while (_ordered.First is { } oldest &&
            oldest.Value.ExpiresAt <= now)
        {
            Remove(oldest);
        }
    }

    private void RemoveOldest()
    {
        if (_ordered.First is { } oldest)
        {
            Remove(oldest);
        }
    }

    private void Remove(LinkedListNode<Attempt> node)
    {
        _byOperation.Remove(node.Value.OperationId);
        _ordered.Remove(node);
    }

    private static void ValidateDigest(
        string? value,
        string parameterName)
    {
        if (value is null ||
            value.Length != CommandEnvelopeContract.DigestHexLength ||
            value.Any(static character =>
                character is not (
                    >= '0' and <= '9' or
                    >= 'A' and <= 'F')))
        {
            throw new ArgumentException(
                "A canonical uppercase SHA-256 digest is required.",
                parameterName);
        }
    }

    private sealed record Attempt(
        string OperationId,
        string RequestHash,
        DateTimeOffset ExpiresAt,
        bool Completed);
}
