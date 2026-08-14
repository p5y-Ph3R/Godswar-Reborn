using System.Collections.Concurrent;

namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Process-lifetime authority for combat attempts that passed admission.
/// Revisions are globally monotonic so pruning actor diagnostics can never
/// make a reconnected character reuse an earlier deterministic roll.
/// </summary>
internal sealed class AdmittedCombatRevisionAuthority
{
    private const int DiagnosticCapacity = 4_096;
    private readonly ConcurrentDictionary<CombatActorKey, ActorRevision>
        _latestByActor = new();
    private readonly ConcurrentQueue<ActorRevisionStamp> _diagnosticOrder =
        new();
    private long _nextRevision;
    private int _queuedDiagnostics;

    public ulong Admit(int accountId, int characterId)
    {
        if (accountId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(accountId));
        }

        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }

        var signedRevision = Interlocked.Increment(ref _nextRevision);
        if (signedRevision <= 0)
        {
            throw new OverflowException(
                "The admitted combat revision authority was exhausted.");
        }

        var revision = checked((ulong)signedRevision);
        var key = new CombatActorKey(accountId, characterId);
        var actorRevision = new ActorRevision(revision);
        _latestByActor[key] = actorRevision;
        _diagnosticOrder.Enqueue(new(key, actorRevision));
        Interlocked.Increment(ref _queuedDiagnostics);
        TrimDiagnostics();
        return revision;
    }

    public bool TryGetLatest(
        int accountId,
        int characterId,
        out ulong revision)
    {
        if (_latestByActor.TryGetValue(
                new CombatActorKey(accountId, characterId),
                out var actorRevision))
        {
            revision = actorRevision.Revision;
            return true;
        }

        revision = 0;
        return false;
    }

    private void TrimDiagnostics()
    {
        while (Volatile.Read(ref _queuedDiagnostics) >
               DiagnosticCapacity &&
               _diagnosticOrder.TryDequeue(out var oldest))
        {
            Interlocked.Decrement(ref _queuedDiagnostics);
            if (_latestByActor.TryGetValue(
                    oldest.Key,
                    out var current) &&
                current == oldest.Revision)
            {
                _latestByActor.TryRemove(
                    new KeyValuePair<CombatActorKey, ActorRevision>(
                        oldest.Key,
                        current));
            }
        }
    }

    private readonly record struct CombatActorKey(
        int AccountId,
        int CharacterId);

    private readonly record struct ActorRevision(ulong Revision);

    private readonly record struct ActorRevisionStamp(
        CombatActorKey Key,
        ActorRevision Revision);
}
