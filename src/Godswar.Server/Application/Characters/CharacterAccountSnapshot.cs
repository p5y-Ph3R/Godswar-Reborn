using System.Collections.Immutable;

namespace Godswar.Server.Application.Characters;

internal sealed record CharacterAccountSnapshot(
    int ContractVersion,
    int AccountId,
    string ProviderSnapshotToken,
    DateTimeOffset ReadAtUtc,
    CharacterSlotPolicy SlotPolicy,
    CharacterLoadSnapshot? Character);

internal sealed record CharacterLoadSnapshot(
    CharacterIdentitySnapshot Identity,
    CharacterAppearanceSnapshot Appearance,
    CharacterLocationSnapshot Location,
    CharacterProgressionSnapshot Progression,
    CharacterVitalsSnapshot Vitals,
    CharacterWalletSnapshot Wallet,
    CharacterLoadoutSnapshot Loadout,
    CharacterZodiacSnapshot Zodiac,
    CharacterCalculatedStatsSnapshot CalculatedStats,
    ImmutableArray<CharacterSkillSnapshot> Skills,
    ImmutableArray<CharacterTalentSnapshot> Talents,
    ImmutableArray<CharacterPetSnapshot> Pets,
    ImmutableArray<CharacterProgressionBoostSnapshot> PersonalBoosts);

internal sealed record CharacterIdentitySnapshot(
    int CharacterId,
    int AccountId,
    string Name,
    DateTimeOffset CreatedAtUtc,
    short CharacterSlot = 0,
    long LifecycleVersion = 1);

internal sealed record CharacterAppearanceSnapshot(
    byte Gender,
    byte Camp,
    byte Profession,
    byte Hair,
    byte Face,
    byte Faith);

internal sealed record CharacterLocationSnapshot(
    byte CurrentMap,
    float PositionX,
    float PositionZ,
    long PositionRevision);

internal sealed record CharacterProgressionSnapshot(
    int Level,
    long Experience,
    int TalentPoints,
    int TalentExperience,
    int HolySuitPoints,
    bool FighterLevelSealed,
    long Revision = 0);

internal static class CharacterProgressionSnapshotRules
{
    public const int MaximumCharacterLevel = 200;
    public const int FighterLevelSealLevel = 89;
}

internal sealed record CharacterVitalsSnapshot(
    int BaseMaxHp,
    int BaseMaxMp,
    int PersistedCurrentHp,
    int PersistedCurrentMp,
    long Revision);

internal sealed record CharacterWalletSnapshot(
    int Silver,
    int Gold);

internal sealed record CharacterLoadoutSnapshot(
    string Equipment,
    string KitBag,
    short WeaponRank,
    int WeaponAuraEffect,
    short ArmorRank,
    int ArmorAuraEffect,
    long InventoryRevision = 0);

internal sealed record CharacterZodiacSnapshot(
    byte Type,
    int LuckyStatus,
    DateTimeOffset? LuckyExpiresAtUtc,
    byte Level,
    int Energy,
    int EnergyRemainderX100,
    DateOnly? OnlineDay,
    long OnlineDurationTicksToday,
    DateTimeOffset? LastOnlineAtUtc,
    DateOnly? LastCompensationDay,
    int AccumulatedExperienceX100,
    int AccumulatedTalentExperienceX100,
    ImmutableArray<int> SkillGridLevels,
    ImmutableArray<int> SkillGridSkillIds);

internal sealed record CharacterSkillSnapshot(
    int SkillId,
    int Level);

internal sealed record CharacterTalentSnapshot(
    int TalentId,
    int Rank,
    int DisplayValue,
    int NextCost);

internal sealed record CharacterProgressionBoostSnapshot(
    int StatusId,
    int Kind,
    int BonusBasisPoints,
    int Priority,
    DateTimeOffset ActivatedAtUtc,
    long? RemainingOnlineTicks,
    string Source);
