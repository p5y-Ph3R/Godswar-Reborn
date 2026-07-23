using System.Collections.Immutable;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.World.Components.Players;

internal readonly record struct PlayerIdentityComponent(
    int CharacterId,
    int AccountId,
    uint ObjectId,
    string Name,
    DateTime CreatedUtc,
    long WorldRevision);

internal readonly record struct PlayerClassComponent(
    byte Gender,
    byte Profession,
    byte Hair,
    byte Face,
    byte Faith);

internal readonly record struct PlayerCampComponent(byte Camp);

internal readonly record struct PlayerTransformComponent(
    byte MapId,
    float X,
    float Z);

internal struct PlayerVitalsComponent
{
    public PlayerVitalsComponent(
        int currentHp,
        int maximumHp,
        int currentMp,
        int maximumMp,
        long revision)
    {
        CurrentHp = currentHp;
        MaximumHp = maximumHp;
        CurrentMp = currentMp;
        MaximumMp = maximumMp;
        Revision = revision;
    }

    public int CurrentHp;
    public int MaximumHp;
    public int CurrentMp;
    public int MaximumMp;
    public long Revision;
}

internal readonly record struct PlayerProgressionComponent(
    int Level,
    int Experience,
    int TalentPoints,
    int TalentExperience,
    int HolySuitPoints);

internal readonly record struct PlayerWalletComponent(
    int Silver,
    int Gold);

/// <summary>
/// The committed equipment projection needed for world appearance. Kit-bag
/// contents remain transaction-authoritative and are deliberately not an ECS
/// component.
/// </summary>
internal readonly record struct PlayerEquipmentAppearanceComponent(
    string Equipment,
    short WeaponRank,
    int WeaponAuraEffect,
    short ArmorRank,
    int ArmorAuraEffect);

internal readonly record struct PlayerZodiacComponent(
    byte Type,
    int LuckyStatus,
    DateTimeOffset? LuckyExpiresAt,
    byte Level,
    int Energy,
    int EnergyRemainderX100,
    DateOnly? OnlineDay,
    long OnlineDurationTicksToday,
    DateTimeOffset? LastOnlineAt,
    DateOnly? LastCompensationDay,
    int AccumulatedExperienceX100,
    int AccumulatedTalentExperienceX100);

internal readonly record struct PlayerStatusEffectsComponent(
    ImmutableArray<ClientStatusEffect> Effects,
    ClientStatusAggregate Aggregate,
    string Fingerprint);
