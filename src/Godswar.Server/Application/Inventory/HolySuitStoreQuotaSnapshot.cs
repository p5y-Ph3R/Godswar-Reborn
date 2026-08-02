namespace Godswar.Server.Application.Inventory;

/// <summary>
/// One bounded, read-only view of the account's current realm-day Holy Suit
/// storage allowance. A battle-pass exemption removes enforcement of
/// <see cref="DailyExperienceCredit"/>; the finite value remains useful for
/// audit and stock-client display.
/// </summary>
internal sealed record HolySuitStoreQuotaSnapshot
{
    public HolySuitStoreQuotaSnapshot(
        int characterId,
        int characterLevel,
        DateOnly usageDay,
        long storedExperienceToday,
        long dailyExperienceCredit,
        bool battlePassDailyLimitExempt)
    {
        if (characterId <= 0 ||
            characterLevel <= 0 ||
            usageDay == default ||
            storedExperienceToday < 0 ||
            dailyExperienceCredit <= 0)
        {
            throw new ArgumentException(
                "The Holy Suit store-quota snapshot is invalid.");
        }

        CharacterId = characterId;
        CharacterLevel = characterLevel;
        UsageDay = usageDay;
        StoredExperienceToday = storedExperienceToday;
        DailyExperienceCredit = dailyExperienceCredit;
        BattlePassDailyLimitExempt = battlePassDailyLimitExempt;
    }

    public int CharacterId { get; }

    public int CharacterLevel { get; }

    public DateOnly UsageDay { get; }

    public long StoredExperienceToday { get; }

    public long DailyExperienceCredit { get; }

    public bool BattlePassDailyLimitExempt { get; }
}
