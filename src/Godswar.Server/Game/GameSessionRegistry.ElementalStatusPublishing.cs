using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private ElementalClientStatusOverlay
        CaptureTrainingDummyElementalStatusOverlay(
            GameSessionContext expected,
            DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(expected.Session, out var current) ||
                !current.WorldReady ||
                current.CharacterId != expected.CharacterId ||
                current.ObjectId != expected.ObjectId ||
                current.WorldInstanceId != expected.WorldInstanceId ||
                current.WorldRevision != expected.WorldRevision ||
                current.Ownership != expected.Ownership ||
                !ReferenceEquals(current.Character, expected.Character) ||
                !_trainingDummies.Contains(current.Character) ||
                !_elementalCombatSessions.TryGetValue(
                    current.Session,
                    out var elemental) ||
                elemental.Identity.CharacterId != current.CharacterId ||
                elemental.Identity.MapId != current.MapId ||
                elemental.Identity.WorldInstanceId !=
                    current.WorldInstanceId ||
                elemental.Identity.Ownership != current.Ownership)
            {
                return ElementalClientStatusOverlay.Empty;
            }

            lock (elemental.Gate)
            {
                var snapshot = elemental.Statuses.CaptureActive(
                    now.ToUnixTimeMilliseconds());
                return ElementalClientStatusProjection.Create(snapshot, now);
            }
        }
    }

    private PlayerStatusSnapshot MergeTrainingDummyElementalStatusOverlay(
        GameSessionContext context,
        PlayerStatusSnapshot baseline,
        DateTimeOffset now,
        out ElementalClientStatusOverlay overlay)
    {
        overlay = CaptureTrainingDummyElementalStatusOverlay(context, now);
        return ElementalClientStatusProjection.Merge(baseline, overlay);
    }

    private async Task<bool>
        PublishTrainingDummyElementalStatusIfChangedAsync(
            GameSessionContext expected,
            DateTimeOffset now,
            string reason,
            CancellationToken cancellationToken)
    {
        var observed = CaptureTrainingDummyElementalStatusOverlay(
            expected,
            now);
        if (!_playerStatusStates.TryGetValue(
                expected.Session,
                out var state))
        {
            if (observed.Effects.Count == 0 ||
                !TryGetOrCreatePlayerStatusState(
                    expected.Session,
                    out state))
            {
                return false;
            }
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var current = CaptureTrainingDummyElementalStatusOverlay(
                expected,
                now);
            if (string.Equals(
                    state.LastPublishedElementalFingerprint,
                    current.Fingerprint,
                    StringComparison.Ordinal))
            {
                return false;
            }

            return await PublishStatusSnapshotLockedAsync(
                expected.Session,
                state,
                now,
                reason,
                force: false,
                broadcast: true,
                cancellationToken);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    internal async Task<int>
        ReconcileTrainingDummyElementalStatusesOnceAsync(
            DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var targets = _sessions.Values
            .Where(context =>
                context.WorldReady &&
                (_trainingDummies.Contains(context.Character) ||
                 (_playerStatusStates.TryGetValue(
                      context.Session,
                      out var state) &&
                  !string.Equals(
                      state.LastPublishedElementalFingerprint,
                      ElementalClientStatusProjection.EmptyFingerprint,
                      StringComparison.Ordinal))))
            .ToArray();
        var published = 0;
        foreach (var target in targets)
        {
            try
            {
                if (await PublishTrainingDummyElementalStatusIfChangedAsync(
                        target,
                        now,
                        "training-dummy-elemental-reconcile",
                        cancellationToken))
                {
                    published++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex is IOException or ObjectDisposedException)
                {
                    Remove(target.Session);
                }
                else
                {
                    Console.WriteLine(
                        "[elemental-status] reconcile deferred " +
                        $"target={target.DisplayName}: {ex.Message}");
                }
            }
        }

        return published;
    }
}
