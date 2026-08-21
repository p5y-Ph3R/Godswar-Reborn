using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private TrainingDummyHostileStatusClientOverlay
        CaptureTrainingDummyHostileStatusOverlay(
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
                !ReferenceEquals(current.Character, expected.Character))
            {
                return TrainingDummyHostileStatusClientOverlay.Empty;
            }

            var snapshot = CaptureTrainingDummyHostileStatusSnapshotLocked(
                current,
                now);
            return TrainingDummyHostileStatusClientProjection.Create(
                snapshot,
                now);
        }
    }

    private PlayerStatusSnapshot MergeTrainingDummyClientStatusOverlays(
        GameSessionContext context,
        PlayerStatusSnapshot baseline,
        DateTimeOffset now,
        out ElementalClientStatusOverlay elementalOverlay,
        out TrainingDummyHostileStatusClientOverlay hostileOverlay)
    {
        var elemental = MergeTrainingDummyElementalStatusOverlay(
            context,
            baseline,
            now,
            out elementalOverlay);
        hostileOverlay = CaptureTrainingDummyHostileStatusOverlay(
            context,
            now);
        return TrainingDummyHostileStatusClientProjection.Merge(
            elemental,
            hostileOverlay);
    }

    internal async Task<bool>
        PublishTrainingDummyHostileStatusApplicationAsync(
            GameSessionContext target,
            HostileStatusApplicationDecision decision,
            DateTimeOffset now,
            string reason,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_playerStatusStates.ContainsKey(target.Session) &&
            CaptureTrainingDummyHostileStatusOverlay(target, now)
                .Effects.Count == 0)
        {
            return false;
        }
        if (!TryGetOrCreatePlayerStatusState(target.Session, out var state))
        {
            return false;
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var published = await PublishStatusSnapshotLockedAsync(
                target.Session,
                state,
                now,
                reason,
                force: false,
                broadcast: true,
                cancellationToken);
            if (decision.Applied && decision.ActiveStatus is { } active)
            {
                ScheduleTrainingDummyHostileStatusExpiry(
                    target,
                    state,
                    active.ExpiresAt,
                    active.Definition.StatusId,
                    active.Revision);
            }
            return published;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private void ScheduleTrainingDummyHostileStatusExpiry(
        GameSessionContext target,
        PlayerStatusState state,
        DateTimeOffset expiresAt,
        uint statusId,
        long revision)
    {
        _ = PublishTrainingDummyHostileStatusExpiryAsync(
            target,
            state,
            expiresAt,
            statusId,
            revision);
    }

    private async Task PublishTrainingDummyHostileStatusExpiryAsync(
        GameSessionContext target,
        PlayerStatusState state,
        DateTimeOffset expiresAt,
        uint statusId,
        long revision)
    {
        try
        {
            var delay = expiresAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, state.Lifetime.Token);
            }

            await state.Gate.WaitAsync(state.Lifetime.Token);
            try
            {
                await PublishStatusSnapshotLockedAsync(
                    target.Session,
                    state,
                    DateTimeOffset.UtcNow,
                    $"training-dummy-hostile-{statusId}-expired-" +
                    revision,
                    force: true,
                    broadcast: true,
                    state.Lifetime.Token);
            }
            finally
            {
                state.Gate.Release();
            }
        }
        catch (OperationCanceledException) when (
            state.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                "[training-status] expiry projection deferred " +
                $"target={target.DisplayName} status={statusId} " +
                $"revision={revision}: {ex.Message}");
        }
    }
}
