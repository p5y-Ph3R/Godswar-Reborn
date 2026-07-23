using Godswar.Server.Packets;

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
                        out var nextRecoveryAt))
                {
                    continue;
                }

                var recoveryStartedAt =
                    nextRecoveryAt - PlayerRecoveryInterval;
                var character = current.Character;
                lock (character.VitalsSync)
                {
                    var decision = GetPlayerRuntimeEcs(
                            current.Session)
                        .Recovery
                        .Evaluate(
                            character,
                            current.ObjectId,
                            recoveryStartedAt,
                            now);
                    _nextPlayerRecoveryAt[current.CharacterId] =
                        decision.NextPulseAt;
                    if (!decision.Recovered)
                    {
                        continue;
                    }

                    character.CurrentHp = decision.CurrentHp;
                    character.CurrentMp = decision.CurrentMp;
                    character.MarkVitalsChanged();
                    if (character.VitalsRevision !=
                        decision.VitalsRevision)
                    {
                        throw new InvalidOperationException(
                            "ECS recovery revision diverged from the " +
                            "authoritative character revision.");
                    }
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
        long vitalsRevision;
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

        if (_store is not null)
        {
            try
            {
                lock (character.VitalsSync)
                {
                    currentHp = character.CurrentHp;
                    currentMp = character.CurrentMp;
                    vitalsRevision = character.VitalsRevision;
                }

                await _store.SaveCharacterVitalsAsync(
                    context.AccountId,
                    context.CharacterId,
                    currentHp,
                    currentMp,
                    vitalsRevision,
                    cancellationToken);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    "[recovery] vitals persistence deferred " +
                    $"character={context.DisplayName}: {ex.Message}");
            }
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
                    cancellationToken);
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
            }
        }
    }
}
