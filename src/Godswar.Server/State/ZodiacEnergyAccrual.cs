namespace Godswar.Server.State;

internal readonly record struct ZodiacEnergyPolicy(
    bool Enabled,
    int TickSeconds,
    int BoostedDailySeconds,
    int BoostedEnergyPerTickX100,
    int NormalEnergyPerTickX100,
    int CompensationOnlineThresholdSeconds,
    int CompensationSeconds,
    int ServerUtcOffsetMinutes)
{
    public TimeSpan ServerUtcOffset => TimeSpan.FromMinutes(ServerUtcOffsetMinutes);

    public void Validate()
    {
        if (TickSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TickSeconds));
        }

        if (BoostedDailySeconds < 0 || BoostedDailySeconds % TickSeconds != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BoostedDailySeconds));
        }

        if (BoostedEnergyPerTickX100 < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BoostedEnergyPerTickX100));
        }

        if (NormalEnergyPerTickX100 < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(NormalEnergyPerTickX100));
        }

        if (CompensationOnlineThresholdSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CompensationOnlineThresholdSeconds));
        }

        if (CompensationSeconds < 0 || CompensationSeconds % TickSeconds != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CompensationSeconds));
        }

        if (ServerUtcOffsetMinutes is < -14 * 60 or > 14 * 60)
        {
            throw new ArgumentOutOfRangeException(nameof(ServerUtcOffsetMinutes));
        }
    }
}

internal sealed record ZodiacEnergyAccrualResult(
    int GainedEnergyX100,
    int CurrentEnergy,
    int CurrentEnergyRemainderX100,
    DateOnly OnlineDay,
    long OnlineDurationTicksToday,
    DateTimeOffset LastOnlineAt,
    DateOnly? LastCompensationDay,
    bool CompensationApplied);

internal static class ZodiacEnergyAccrual
{
    public static ZodiacEnergyAccrualResult Apply(
        GameCharacter character,
        DateTimeOffset onlineFrom,
        DateTimeOffset onlineUntil,
        ZodiacEnergyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(character);
        policy.Validate();

        if (onlineUntil < onlineFrom)
        {
            throw new ArgumentOutOfRangeException(
                nameof(onlineUntil),
                "Online interval cannot end before it starts.");
        }

        // Concurrent/replaced sessions can flush out of order. Never move the
        // durable watermark backwards or a later reconnect could double-count
        // an interval already credited by the newer session.
        if (character.ZodiacLastOnlineAt is { } persistedLastOnlineAt &&
            persistedLastOnlineAt > onlineUntil)
        {
            var persistedDay = character.ZodiacOnlineDay ??
                GetServerDay(persistedLastOnlineAt, policy.ServerUtcOffset);
            character.ZodiacOnlineDay = persistedDay;
            return new ZodiacEnergyAccrualResult(
                0,
                character.ZodiacEnergy,
                character.ZodiacEnergyRemainderX100,
                persistedDay,
                character.ZodiacOnlineDurationTicksToday,
                persistedLastOnlineAt,
                character.ZodiacLastCompensationDay,
                false);
        }

        var cursor = onlineFrom;
        if (character.ZodiacLastOnlineAt is { } lastOnlineAt && lastOnlineAt > cursor)
        {
            cursor = lastOnlineAt;
        }

        if (cursor > onlineUntil)
        {
            cursor = onlineUntil;
        }

        var gainedEnergyX100 = 0;
        var compensationApplied = false;
        EnsureDay(
            character,
            GetServerDay(cursor, policy.ServerUtcOffset),
            policy,
            ref gainedEnergyX100,
            ref compensationApplied);

        while (cursor < onlineUntil)
        {
            var serverDay = GetServerDay(cursor, policy.ServerUtcOffset);
            EnsureDay(
                character,
                serverDay,
                policy,
                ref gainedEnergyX100,
                ref compensationApplied);

            var nextDay = new DateTimeOffset(
                serverDay.AddDays(1).ToDateTime(TimeOnly.MinValue),
                policy.ServerUtcOffset);
            var segmentEnd = onlineUntil < nextDay ? onlineUntil : nextDay;
            var durationTicks = Math.Max(0L, (segmentEnd - cursor).Ticks);
            if (policy.Enabled && durationTicks > 0)
            {
                gainedEnergyX100 = checked(
                    gainedEnergyX100 + ApplyOnlineDuration(character, durationTicks, policy));
            }
            else
            {
                character.ZodiacOnlineDurationTicksToday = checked(
                    character.ZodiacOnlineDurationTicksToday + durationTicks);
            }

            cursor = segmentEnd;
        }

        // An interval ending exactly at original-server midnight belongs to the
        // new day. Rotate the persisted day now so a disconnect at that instant
        // cannot defer or duplicate compensation on the next login.
        EnsureDay(
            character,
            GetServerDay(onlineUntil, policy.ServerUtcOffset),
            policy,
            ref gainedEnergyX100,
            ref compensationApplied);
        character.ZodiacLastOnlineAt = onlineUntil;

        return new ZodiacEnergyAccrualResult(
            gainedEnergyX100,
            character.ZodiacEnergy,
            character.ZodiacEnergyRemainderX100,
            character.ZodiacOnlineDay!.Value,
            character.ZodiacOnlineDurationTicksToday,
            onlineUntil,
            character.ZodiacLastCompensationDay,
            compensationApplied);
    }

    internal static DateOnly GetServerDay(DateTimeOffset instant, TimeSpan serverUtcOffset)
    {
        return DateOnly.FromDateTime(instant.ToOffset(serverUtcOffset).DateTime);
    }

    private static int ApplyOnlineDuration(
        GameCharacter character,
        long durationTicks,
        ZodiacEnergyPolicy policy)
    {
        var previousDurationTicks = Math.Max(0L, character.ZodiacOnlineDurationTicksToday);
        var currentDurationTicks = checked(previousDurationTicks + durationTicks);
        character.ZodiacOnlineDurationTicksToday = currentDurationTicks;

        var tickDurationTicks = checked(TimeSpan.TicksPerSecond * (long)policy.TickSeconds);
        var previousCompletedTicks = previousDurationTicks / tickDurationTicks;
        var currentCompletedTicks = currentDurationTicks / tickDurationTicks;
        if (currentCompletedTicks <= previousCompletedTicks)
        {
            return 0;
        }

        var boostedTickLimit = policy.BoostedDailySeconds / policy.TickSeconds;
        var previousBoostedTicks = Math.Min(previousCompletedTicks, boostedTickLimit);
        var currentBoostedTicks = Math.Min(currentCompletedTicks, boostedTickLimit);
        var boostedTicks = currentBoostedTicks - previousBoostedTicks;
        var normalTicks = (currentCompletedTicks - previousCompletedTicks) - boostedTicks;
        var requestedGainX100 = checked(
            boostedTicks * policy.BoostedEnergyPerTickX100 +
            normalTicks * policy.NormalEnergyPerTickX100);
        return AddEnergyX100(character, checked((int)requestedGainX100));
    }

    private static void EnsureDay(
        GameCharacter character,
        DateOnly serverDay,
        ZodiacEnergyPolicy policy,
        ref int gainedEnergyX100,
        ref bool compensationApplied)
    {
        if (character.ZodiacOnlineDay is null)
        {
            character.ZodiacOnlineDay = serverDay;
            character.ZodiacOnlineDurationTicksToday = 0;
            return;
        }

        var previousDay = character.ZodiacOnlineDay.Value;
        if (previousDay == serverDay)
        {
            return;
        }

        if (previousDay > serverDay)
        {
            // A backwards wall-clock/configuration jump starts a fresh accounting
            // day without manufacturing a compensation award.
            character.ZodiacOnlineDay = serverDay;
            character.ZodiacOnlineDurationTicksToday = 0;
            return;
        }

        var gapDays = serverDay.DayNumber - previousDay.DayNumber;
        var priorOnlineThresholdTicks = checked(
            TimeSpan.TicksPerSecond * (long)policy.CompensationOnlineThresholdSeconds);
        var eligibleForCompensation = gapDays > 1 ||
            character.ZodiacOnlineDurationTicksToday < priorOnlineThresholdTicks;
        if (policy.Enabled &&
            eligibleForCompensation &&
            character.ZodiacLastCompensationDay != serverDay)
        {
            var compensationTicks = policy.CompensationSeconds / policy.TickSeconds;
            var requestedCompensationX100 = checked(
                compensationTicks * policy.BoostedEnergyPerTickX100);
            var appliedCompensationX100 = AddEnergyX100(character, requestedCompensationX100);
            gainedEnergyX100 = checked(gainedEnergyX100 + appliedCompensationX100);
            character.ZodiacLastCompensationDay = serverDay;
            compensationApplied = true;
        }

        character.ZodiacOnlineDay = serverDay;
        character.ZodiacOnlineDurationTicksToday = 0;
    }

    private static int AddEnergyX100(GameCharacter character, int requestedGainX100)
    {
        if (requestedGainX100 <= 0)
        {
            return 0;
        }

        var zodiacLevel = Math.Clamp((int)character.ZodiacLevel, 1, 30);
        var capacityX100 = checked((long)ZodiacEnergyCatalog.GetStorageLimit(zodiacLevel) * 100L);
        var currentX100 = checked(
            Math.Max(0L, character.ZodiacEnergy) * 100L +
            Math.Clamp(character.ZodiacEnergyRemainderX100, 0, 99));
        currentX100 = Math.Min(currentX100, capacityX100);
        var updatedX100 = Math.Min(capacityX100, checked(currentX100 + requestedGainX100));
        character.ZodiacEnergy = checked((int)(updatedX100 / 100));
        character.ZodiacEnergyRemainderX100 = checked((int)(updatedX100 % 100));
        return checked((int)(updatedX100 - currentX100));
    }
}
