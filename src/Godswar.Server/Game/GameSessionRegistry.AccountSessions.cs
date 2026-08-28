using System.Collections.Concurrent;
using Godswar.Server.Application.Characters;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<int, AccountSessionRegistration>
        _accountSessions = [];
    private readonly Dictionary<int, CheckpointOwnershipGate>
        _checkpointOwnershipGates = [];

    public ClientSession? ReplaceAccountSession(
        int accountId,
        ClientSession session)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentNullException.ThrowIfNull(session);
        session.RegisterEgressTerminalObserver(RemoveEgressTerminalSession);

        lock (_gate)
        {
            return ReplaceAccountSessionLocked(
                accountId,
                session);
        }
    }

    private void RemoveEgressTerminalSession(ClientSession session)
    {
        try
        {
            Remove(session);
        }
        catch
        {
            // Session routing already rejects the terminal epoch. The normal
            // connection owner retains its idempotent removal fallback.
        }
    }

    internal AccountSessionReplacement
        ReplaceAccountSessionAndDetachWorld(
            int accountId,
            ClientSession session)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentNullException.ThrowIfNull(session);
        session.RegisterEgressTerminalObserver(RemoveEgressTerminalSession);

        lock (_gate)
        {
            _accountSessions.TryGetValue(
                accountId,
                out var existing);
            if (existing is null ||
                ReferenceEquals(existing.Session, session))
            {
                return new AccountSessionReplacement(
                    ReplaceAccountSessionLocked(
                        accountId,
                        session),
                    DetachedWorld: null);
            }

            var replacedSession = existing.Session;
            DetachedPlayerWorldSession? detachedWorld = null;
            if (_sessions.TryGetValue(
                    replacedSession,
                    out var context))
            {
                detachedWorld =
                    ReserveDetachedPlayerWorldLocked(context);
                try
                {
                    if (!RemoveCore(
                            replacedSession,
                            expectedOwnership: null,
                            preservePlayerStatus: false))
                    {
                        throw new InvalidOperationException(
                            "The replaced world session could not be detached.");
                    }
                }
                catch
                {
                    ReleaseDetachedPlayerWorld(detachedWorld);
                    throw;
                }
            }

            try
            {
                ReplaceAccountSessionLocked(
                    accountId,
                    session);
            }
            catch
            {
                if (detachedWorld is not null)
                {
                    ReleaseDetachedPlayerWorld(detachedWorld);
                }

                throw;
            }
            return new AccountSessionReplacement(
                replacedSession,
                detachedWorld);
        }
    }

    private ClientSession? ReplaceAccountSessionLocked(
        int accountId,
        ClientSession session)
    {
        ClientSession? replaced = null;
        _accountSessions.AddOrUpdate(
            accountId,
            _ => new AccountSessionRegistration(session, default),
            (_, existing) =>
            {
                if (!ReferenceEquals(existing.Session, session))
                {
                    replaced = existing.Session;
                    return new AccountSessionRegistration(
                        session,
                        default);
                }

                return existing;
            });

        return replaced;
    }

    public bool TryBindAccountSessionOwnership(
        int accountId,
        ClientSession session,
        PlayerOwnershipFence ownership)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentNullException.ThrowIfNull(session);
        ownership.Validate();

        while (_accountSessions.TryGetValue(
                   accountId,
                   out var existing))
        {
            if (!ReferenceEquals(existing.Session, session))
            {
                return false;
            }
            if (existing.Ownership == ownership)
            {
                return true;
            }
            if (existing.Ownership.IsValid &&
                existing.Ownership.Generation >= ownership.Generation)
            {
                return false;
            }

            var updated = existing with { Ownership = ownership };
            if (_accountSessions.TryUpdate(
                    accountId,
                    updated,
                    existing))
            {
                return true;
            }
        }

        return false;
    }

    public bool RemoveAccountSession(
        int accountId,
        ClientSession session)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentNullException.ThrowIfNull(session);
        return _accountSessions.TryGetValue(
                   accountId,
                   out var existing) &&
            ReferenceEquals(existing.Session, session) &&
            _accountSessions.TryRemove(
                new KeyValuePair<int, AccountSessionRegistration>(
                    accountId,
                    existing));
    }

    public bool IsCurrentAccountSession(
        int accountId,
        ClientSession session)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentNullException.ThrowIfNull(session);
        return _accountSessions.TryGetValue(
                   accountId,
                   out var existing) &&
            ReferenceEquals(existing.Session, session);
    }

    public bool IsCurrentAccountSession(
        int accountId,
        ClientSession session,
        PlayerOwnershipFence ownership)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentNullException.ThrowIfNull(session);
        if (!ownership.IsValid)
        {
            return false;
        }

        return _accountSessions.TryGetValue(
                   accountId,
                   out var existing) &&
            ReferenceEquals(existing.Session, session) &&
            existing.Ownership == ownership;
    }

    internal bool TryGetAccountSessionOwnership(
        int accountId,
        ClientSession session,
        out PlayerOwnershipFence ownership)
    {
        ownership = default;
        if (!_accountSessions.TryGetValue(
                accountId,
                out var existing) ||
            !ReferenceEquals(existing.Session, session) ||
            !existing.Ownership.IsValid)
        {
            return false;
        }

        ownership = existing.Ownership;
        return true;
    }

    internal async Task<AccountCheckpointAcquisitionScope?>
        EnterAccountCheckpointAcquisitionAsync(
            int accountId,
            ClientSession session,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentNullException.ThrowIfNull(session);

        CheckpointOwnershipGate gate;
        lock (_gate)
        {
            if (!_checkpointOwnershipGates.TryGetValue(
                    accountId,
                    out gate!))
            {
                gate = new CheckpointOwnershipGate();
                _checkpointOwnershipGates.Add(accountId, gate);
            }
            gate.References++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            ReleaseCheckpointOwnershipGateReference(
                accountId,
                gate);
            throw;
        }

        var scope = new AccountCheckpointAcquisitionScope(
            this,
            accountId,
            session,
            gate);
        if (scope.IsCurrent)
        {
            return scope;
        }

        scope.Dispose();
        return null;
    }

    private void ExitCheckpointOwnershipGate(
        int accountId,
        CheckpointOwnershipGate gate)
    {
        gate.Semaphore.Release();
        ReleaseCheckpointOwnershipGateReference(accountId, gate);
    }

    private void ReleaseCheckpointOwnershipGateReference(
        int accountId,
        CheckpointOwnershipGate gate)
    {
        lock (_gate)
        {
            if (gate.References <= 0)
            {
                throw new InvalidOperationException(
                    "Checkpoint ownership gate reference accounting " +
                    "underflowed.");
            }

            gate.References--;
            if (gate.References == 0)
            {
                if (!_checkpointOwnershipGates.TryGetValue(
                        accountId,
                        out var current) ||
                    !ReferenceEquals(current, gate) ||
                    !_checkpointOwnershipGates.Remove(accountId))
                {
                    throw new InvalidOperationException(
                        "Checkpoint ownership gate identity changed " +
                        "while it was referenced.");
                }
            }
        }
    }

    internal sealed class AccountCheckpointAcquisitionScope :
        IDisposable
    {
        private readonly int _accountId;
        private readonly CheckpointOwnershipGate _gate;
        private readonly GameSessionRegistry _registry;
        private readonly ClientSession _session;
        private int _disposed;

        internal AccountCheckpointAcquisitionScope(
            GameSessionRegistry registry,
            int accountId,
            ClientSession session,
            CheckpointOwnershipGate gate)
        {
            _registry = registry;
            _accountId = accountId;
            _session = session;
            _gate = gate;
        }

        public bool IsCurrent =>
            Volatile.Read(ref _disposed) == 0 &&
            _registry.IsCurrentAccountSession(
                _accountId,
                _session);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _registry.ExitCheckpointOwnershipGate(
                _accountId,
                _gate);
        }
    }

    internal sealed class CheckpointOwnershipGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int References { get; set; }
    }

    private sealed record AccountSessionRegistration(
        ClientSession Session,
        PlayerOwnershipFence Ownership);
}

internal readonly record struct AccountSessionReplacement(
    ClientSession? ReplacedSession,
    DetachedPlayerWorldSession? DetachedWorld);
