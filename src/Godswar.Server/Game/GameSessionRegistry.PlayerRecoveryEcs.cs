using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private async Task AdvancePlayerRuntimeEcsOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var recovered = new List<GameSessionContext>();
        foreach (var snapshot in _sessions.Values)
        {
            lock (_gate)
            {
                if (!_sessions.TryGetValue(
                        snapshot.Session,
                        out var current) ||
                    !current.WorldReady ||
                    !_nextPlayerRecoveryAt.TryGetValue(
                        current.CharacterId,
                        out var recoveryDeadline))
                {
                    continue;
                }

                var nextRecoveryAt = recoveryDeadline.Read();
                var recoveryStartedAt =
                    nextRecoveryAt - PlayerRecoveryInterval;
                var character = current.Character;
                var decision = GetPlayerRuntimeEcs(
                        current.Session)
                    .Recovery
                    .Evaluate(
                        character,
                        current.ObjectId,
                        recoveryStartedAt,
                        now);
                recoveryDeadline.Write(decision.NextPulseAt);
                if (!decision.PulseAccepted)
                {
                    continue;
                }

                var recovery = ApplyAuthoritativeRecoveryPulseLocked(
                    current,
                    now,
                    PlayerRecoveryCatalog.GetTotalHp(character),
                    PlayerRecoveryCatalog.GetTotalMp(character));
                if (!recovery.VitalsChanged)
                {
                    continue;
                }

                recovered.Add(current);
            }
        }

        foreach (var context in recovered)
        {
            await PublishRecoveredVitalsAsync(
                context,
                cancellationToken);
        }

        await AdvancePlayerStatusEcsOnceAsync(
            now,
            cancellationToken);
    }

    private async Task PublishRecoveredVitalsAsync(
        GameSessionContext context,
        CancellationToken cancellationToken)
    {
        var character = context.Character;
        int currentHp;
        int currentMp;
        lock (character.VitalsSync)
        {
            currentHp = character.CurrentHp;
            currentMp = character.CurrentMp;
        }

        try
        {
            await context.Session.SendAsync(
                PacketBuilder.PlayerVitalsUpdate(
                    LocalPlayerObjectId,
                    currentHp,
                    currentMp),
                cancellationToken,
                "PlayerPassiveRecoverySelf");
            await BroadcastToMapAsync(
                character.CurrentMap,
                PacketBuilder.PlayerVitalsUpdate(
                    context.ObjectId,
                    currentHp,
                    currentMp),
                cancellationToken,
                context.Session,
                "PlayerPassiveRecoveryWorld");
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException)
        {
            Remove(context.Session);
        }

        try
        {
            await PersistRoutineVitalsAsync(
                context,
                cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException)
        {
            Console.WriteLine(
                "[recovery] vitals persistence deferred " +
                $"character={context.DisplayName}: {ex.Message}");
        }

        lock (character.VitalsSync)
        {
            currentHp = character.CurrentHp;
            currentMp = character.CurrentMp;
        }

        Console.WriteLine(
            $"[recovery] character={context.DisplayName} " +
            $"hp={currentHp}/{character.MaxHp} " +
            $"mp={currentMp}/{character.MaxMp}");
    }

    private async Task AdvancePlayerStatusEcsOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var pair in _playerStatusStates.ToArray())
        {
            var session = pair.Key;
            var state = pair.Value;
            var admissionClaims = new ExactStatusDisconnectClaims();
            await state.Gate.WaitAsync(cancellationToken);
            try
            {
                if (!_sessions.ContainsKey(session))
                {
                    continue;
                }

                await PublishStatusSnapshotLockedAsync(
                    session,
                    state,
                    now,
                    "ecs-status-clock",
                    force: false,
                    broadcast: true,
                    cancellationToken,
                    claimedDisconnects: admissionClaims);
            }
            catch (Exception ex) when (
                ex is IOException or ObjectDisposedException)
            {
                Remove(session);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    "[status] ECS expiry reconciliation failed: " +
                    ex.Message);
            }
            finally
            {
                state.Gate.Release();
                admissionClaims.CompleteAll(this);
            }
        }
    }
}
