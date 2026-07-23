using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public async Task RunExperienceBoostStatusReconciliationAsync(
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ExperienceBoostStatusReconciliationInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await ReconcileExperienceBoostStatusesOnceAsync(
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task<ExperienceBoostState> GetExperienceBoostStateAsync(
        ClientSession session,
        int accountId,
        int characterId,
        byte camp,
        byte mapId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return ExperienceBoostState.Empty;
        }

        await CheckpointProgressionBoostOnlineTimeAsync(
            session,
            now,
            cancellationToken);
        return await _store.GetExperienceBoostStateAsync(
            accountId,
            characterId,
            camp,
            mapId,
            now,
            cancellationToken);
    }

    public async Task FinishProgressionBoostOnlineSessionAsync(
        ClientSession session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!_progressionBoostOnlineSessions.TryRemove(session, out var state))
        {
            return;
        }

        try
        {
            if (_progressionBoostCharacterOwners.TryGetValue(state.CharacterId, out var owner) &&
                ReferenceEquals(owner, session))
            {
                await ConsumeProgressionBoostOnlineTimeAsync(
                    session,
                    state,
                    now,
                    cancellationToken);
            }
        }
        finally
        {
            // Never leak ownership after a failed persistence attempt. The
            // periodic checkpoint limits the unpersisted tail to one cycle,
            // and a replacement session must always be able to take ownership.
            _progressionBoostCharacterOwners.TryRemove(
                new KeyValuePair<int, ClientSession>(state.CharacterId, session));
        }
    }

    private async Task CheckpointProgressionBoostOnlineTimeAsync(
        ClientSession session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_store is null ||
            !_progressionBoostOnlineSessions.TryGetValue(session, out var state) ||
            !_progressionBoostCharacterOwners.TryGetValue(state.CharacterId, out var owner) ||
            !ReferenceEquals(owner, session))
        {
            return;
        }

        await ConsumeProgressionBoostOnlineTimeAsync(
            session,
            state,
            now,
            cancellationToken);
    }

    private async Task ConsumeProgressionBoostOnlineTimeAsync(
        ClientSession session,
        ProgressionBoostOnlineSessionState state,
        DateTimeOffset onlineUntil,
        CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return;
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (onlineUntil <= state.LastAccountedAt)
            {
                return;
            }

            var onlineFrom = state.LastAccountedAt;
            await _store.ConsumeCharacterBoostOnlineTimeAsync(
                state.AccountId,
                state.CharacterId,
                onlineFrom,
                onlineUntil,
                cancellationToken);
            state.LastAccountedAt = onlineUntil;
            ObserveCommittedOnlineDurationEcs(
                session,
                state.AccountId,
                state.CharacterId,
                Godswar.Server.World.Components.Players
                    .PlayerOnlineDurationTarget
                    .ProgressionBoosts,
                onlineFrom,
                onlineUntil);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task RunZodiacEnergyAccrualAsync(CancellationToken cancellationToken)
    {
        if (!_zodiacEnergyPolicy.Enabled || _store is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(_zodiacPersistenceInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await AdvanceZodiacEnergyAccrualOnceAsync(
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal async Task<int> AdvanceZodiacEnergyAccrualOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!_zodiacEnergyPolicy.Enabled || _store is null)
        {
            return 0;
        }

        var notifications = 0;
        foreach (var context in _sessions.Values.Where(static context => context.WorldReady))
        {
            if (!_zodiacOnlineSessions.TryGetValue(context.Session, out var state))
            {
                continue;
            }

            try
            {
                if (await PersistZodiacOnlineTimeAsync(
                        context.Session,
                        state,
                        now,
                        sendNotification: true,
                        cancellationToken))
                {
                    notifications++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[zodiac] online accrual deferred character={state.Character.Name}: {ex.Message}");
            }
        }

        return notifications;
    }

    public async Task FinishZodiacOnlineSessionAsync(
        ClientSession session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!_zodiacOnlineSessions.TryRemove(session, out var state) ||
            !_zodiacEnergyPolicy.Enabled ||
            _store is null)
        {
            return;
        }

        await PersistZodiacOnlineTimeAsync(
            session,
            state,
            now,
            sendNotification: false,
            cancellationToken);
    }

    private async Task<bool> PersistZodiacOnlineTimeAsync(
        ClientSession session,
        ZodiacOnlineSessionState state,
        DateTimeOffset onlineUntil,
        bool sendNotification,
        CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return false;
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (onlineUntil <= state.LastAccountedAt)
            {
                return false;
            }

            var onlineFrom = state.LastAccountedAt;
            var result = await _store.ApplyZodiacOnlineTimeAsync(
                state.AccountId,
                state.CharacterId,
                onlineFrom,
                onlineUntil,
                _zodiacEnergyPolicy,
                cancellationToken);
            if (result is null)
            {
                return false;
            }

            state.LastAccountedAt = result.LastOnlineAt;
            ObserveCommittedOnlineDurationEcs(
                session,
                state.AccountId,
                state.CharacterId,
                Godswar.Server.World.Components.Players
                    .PlayerOnlineDurationTarget
                    .Zodiac,
                onlineFrom,
                result.LastOnlineAt);
            lock (state.Character.ZodiacSync)
            {
                state.Character.ZodiacEnergy = result.CurrentEnergy;
                state.Character.ZodiacEnergyRemainderX100 = result.CurrentEnergyRemainderX100;
                state.Character.ZodiacOnlineDay = result.OnlineDay;
                state.Character.ZodiacOnlineDurationTicksToday = result.OnlineDurationTicksToday;
                state.Character.ZodiacLastOnlineAt = result.LastOnlineAt;
                state.Character.ZodiacLastCompensationDay = result.LastCompensationDay;
            }

            if (!sendNotification || result.GainedEnergyX100 <= 0)
            {
                return false;
            }

            await session.SendAsync(
                PacketBuilder.ZodiacEnergyIncrease(
                    result.CurrentEnergy,
                    result.GainedEnergyX100),
                cancellationToken,
                result.CompensationApplied
                    ? "ZodiacEnergyCompensation"
                    : "ZodiacEnergyIncrease");
            Console.WriteLine(
                $"[zodiac] energy character={state.Character.Name} gain={result.GainedEnergyX100 / 100m:0.##} total={result.CurrentEnergy}.{result.CurrentEnergyRemainderX100:00} compensation={result.CompensationApplied}");
            return true;
        }
        finally
        {
            state.Gate.Release();
        }
    }

}
