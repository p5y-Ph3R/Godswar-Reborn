using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public async Task RunMonsterRoamingAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MonsterMapRuntime.TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await AdvanceMonsterWorldOnceAsync(DateTimeOffset.UtcNow, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task RunPlayerRecoveryAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PlayerRecoveryPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await AdvancePlayerRecoveryOnceAsync(DateTimeOffset.UtcNow, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal async Task AdvancePlayerRecoveryOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
        {
            await AdvancePlayerRuntimeEcsOnceAsync(
                now,
                cancellationToken);
            return;
        }

        var recovered = new List<GameSessionContext>();
        foreach (var snapshot in _sessions.Values)
        {
            lock (_gate)
            {
                if (!_sessions.TryGetValue(snapshot.Session, out var current) ||
                    !current.WorldReady ||
                    !_nextPlayerRecoveryAt.TryGetValue(current.CharacterId, out var nextRecoveryAt) ||
                    now < nextRecoveryAt)
                {
                    continue;
                }

                _nextPlayerRecoveryAt[current.CharacterId] = now + PlayerRecoveryInterval;
                if (PlayerRecoveryCatalog.TryApply(current.Character))
                {
                    recovered.Add(current);
                }
            }
        }

        foreach (var context in recovered)
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
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine(
                        $"[recovery] vitals persistence deferred character={context.DisplayName}: {ex.Message}");
                }
            }

            lock (character.VitalsSync)
            {
                currentHp = character.CurrentHp;
                currentMp = character.CurrentMp;
            }

            Console.WriteLine(
                $"[recovery] character={context.DisplayName} hp={currentHp}/{character.MaxHp} mp={currentMp}/{character.MaxMp}");
        }
    }

}
