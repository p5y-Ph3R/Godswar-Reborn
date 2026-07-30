using System.Collections.Immutable;
using System.Text.Json;
using Godswar.Server.Application.Characters;

namespace Godswar.Server.State;

internal sealed partial class JsonGameStore
{
    public async Task<CharacterAccountSnapshot> ReadAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
        {
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.InvalidData,
                "A character snapshot requires a positive account ID.");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var database = await LoadUnsafeAsync(cancellationToken);
            var readAt = DateTimeOffset.UtcNow;
            if (!database.Accounts.Any(account => account.Id == accountId))
            {
                throw new CharacterSnapshotUnavailableException(
                    CharacterSnapshotFailureReason.AccountNotFound,
                    "The authenticated account no longer exists.");
            }

            var characters = database.Characters
                .Where(character => character.AccountId == accountId)
                .OrderBy(character => character.Id)
                .Take(2)
                .ToArray();
            if (characters.Length > 1)
            {
                throw new CharacterSnapshotUnavailableException(
                    CharacterSnapshotFailureReason.AmbiguousCharacterSlot,
                    "SingleCharacterV1 found more than one character.");
            }

            var snapshot = new CharacterAccountSnapshot(
                CharacterSnapshotContractVersions.Current,
                accountId,
                CreateSnapshotTokenUnsafe(readAt),
                readAt,
                CharacterSlotPolicy.SingleCharacterV1,
                characters.Length == 0
                    ? null
                    : MapCharacterSnapshot(
                        database,
                        characters[0],
                        readAt));
            CharacterSnapshotContract.Validate(snapshot);
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CharacterSnapshotUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
                InvalidDataException or
                InvalidCastException or
                OverflowException)
        {
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.InvalidData,
                "The JSON store returned an invalid character snapshot.",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.ProviderUnavailable,
                "JSON character snapshot loading is unavailable.",
                exception);
        }
        finally
        {
            _lock.Release();
        }
    }

    private string CreateSnapshotTokenUnsafe(DateTimeOffset readAt)
    {
        var info = new FileInfo(_statePath);
        return info.Exists
            ? FormattableString.Invariant(
                $"json-v1:{info.LastWriteTimeUtc.Ticks:x16}:{info.Length:x16}")
            : FormattableString.Invariant(
                $"json-v1:memory:{readAt.UtcDateTime.Ticks:x16}");
    }

    private static CharacterLoadSnapshot MapCharacterSnapshot(
        GameDatabase database,
        GameCharacter character,
        DateTimeOffset readAt)
    {
        var skills = MapSkills(character);
        var loadout = new CharacterLoadoutSnapshot(
            string.IsNullOrWhiteSpace(character.Equipment)
                ? GameDefaults.DefaultEquipment(character.Profession)
                : character.Equipment,
            string.IsNullOrWhiteSpace(character.KitBag)
                ? GameDefaults.EmptyKitBag
                : character.KitBag,
            character.WeaponRank,
            character.WeaponAuraEffect,
            character.ArmorRank,
            character.ArmorAuraEffect);
        var vitals = new CharacterVitalsSnapshot(
            character.MaxHp,
            character.MaxMp,
            character.CurrentHp,
            character.CurrentMp,
            character.VitalsRevision);

        return new CharacterLoadSnapshot(
            new CharacterIdentitySnapshot(
                character.Id,
                character.AccountId,
                character.Name,
                ToUtcOffset(character.CreatedUtc)),
            new CharacterAppearanceSnapshot(
                character.Gender,
                character.Camp,
                character.Profession,
                character.Hair,
                character.Face,
                character.Faith),
            new CharacterLocationSnapshot(
                character.CurrentMap,
                character.PositionX,
                character.PositionZ,
                character.PositionRevision),
            new CharacterProgressionSnapshot(
                character.Level,
                character.Experience,
                character.TalentPoints,
                character.TalentExperience,
                character.HolySuitPoints),
            vitals,
            new CharacterWalletSnapshot(character.Silver, character.Gold),
            loadout,
            MapZodiac(character),
            MapCalculatedStats(character, skills.Length),
            skills,
            MapTalents(database, character),
            ImmutableArray<CharacterPetSnapshot>.Empty,
            MapPersonalBoosts(database, character.Id, readAt));
    }

    private static CharacterZodiacSnapshot MapZodiac(
        GameCharacter character) =>
        new(
            character.ZodiacType,
            character.ZodiacLuckyStatus,
            ToUtcOffset(character.ZodiacLuckyExpiresAt),
            character.ZodiacLevel,
            character.ZodiacEnergy,
            character.ZodiacEnergyRemainderX100,
            character.ZodiacOnlineDay,
            character.ZodiacOnlineDurationTicksToday,
            ToUtcOffset(character.ZodiacLastOnlineAt),
            character.ZodiacLastCompensationDay,
            character.ZodiacAccumulatedExperienceX100,
            character.ZodiacAccumulatedTalentExperienceX100,
            ImmutableArray.CreateRange(
                ZodiacSkillGridActivation.NormalizeLevels(
                    character.ZodiacSkillGridLevels)),
            ImmutableArray.CreateRange(
                ZodiacSkillGridActivation.NormalizeSkillIds(
                    character.ZodiacSkillGridSkillIds)));

    private static CharacterCalculatedStatsSnapshot MapCalculatedStats(
        GameCharacter character,
        int learnedSkillCount)
    {
        var stats = CharacterStats.FromCharacter(character);
        return new CharacterCalculatedStatsSnapshot(
            stats.CharacterId,
            stats.AccountId,
            stats.Name,
            stats.Level,
            stats.MaxHp,
            stats.MaxMp,
            Math.Clamp(
                stats.CurrentHp,
                0,
                Math.Max(1, stats.MaxHp)),
            Math.Clamp(
                stats.CurrentMp,
                0,
                Math.Max(1, stats.MaxMp)),
            stats.PhysicalAttack,
            stats.PhysicalDefense,
            stats.MagicAttack,
            stats.MagicDefense,
            stats.Hit,
            stats.Dodge,
            stats.Critical,
            stats.CriticalResistance,
            stats.DamageAbsorb,
            stats.PhysicalDamageBonus,
            stats.MagicDamageBonus,
            stats.CureBonus,
            stats.BeCureBonus,
            stats.HpRecovery,
            stats.MpRecovery,
            stats.IgnorePhysicalDefense,
            stats.IgnoreMagicDefense,
            stats.PhysicalAppendDamage,
            stats.MagicAppendDamage,
            stats.CriticalDamagePercent,
            stats.CriticalDamageFlat,
            stats.WeaponScore,
            stats.WeaponRank,
            stats.WeaponAuraEffect,
            stats.ArmorScore,
            stats.ArmorRank,
            stats.ArmorAuraEffect,
            learnedSkillCount);
    }

    private static ImmutableArray<CharacterSkillSnapshot> MapSkills(
        GameCharacter character) =>
        SkillTalentSeeds.Skills
            .Where(skill =>
                (skill.SkillId == MountCatalog.RideSkillId ||
                 (skill.PreviousSkillId is null &&
                  skill.SkillLevel == 1 &&
                  (skill.MinLevel ?? 1) <= character.Level)) &&
                skill.ClassIds.Contains((short)character.Profession))
            .OrderBy(skill => skill.SkillId)
            .Select(skill => new CharacterSkillSnapshot(
                skill.SkillId,
                skill.SkillId == MountCatalog.RideSkillId
                    ? 1
                    : skill.SkillLevel!.Value))
            .ToImmutableArray();

    private static ImmutableArray<CharacterTalentSnapshot> MapTalents(
        GameDatabase database,
        GameCharacter character)
    {
        var savedRanks = database.CharacterTalents
            .Where(talent => talent.CharacterId == character.Id)
            .GroupBy(talent => talent.TalentId)
            .ToDictionary(
                group => group.Key,
                group => Math.Clamp(
                    group.Max(talent => talent.Rank),
                    0,
                    CharacterTalentProjection.RankCap));
        return SkillTalentSeeds.Talents
            .Where(talent => talent.ClassId == character.Profession)
            .OrderBy(talent => talent.TreeOrder)
            .ThenBy(talent => talent.Id)
            .Select(talent => CharacterTalentProjection.FromPersistedRank(
                talent.Id,
                savedRanks.GetValueOrDefault(talent.Id)))
            .ToImmutableArray();
    }

    private static ImmutableArray<CharacterProgressionBoostSnapshot>
        MapPersonalBoosts(
            GameDatabase database,
            int characterId,
            DateTimeOffset readAt) =>
        database.CharacterExperienceBoosts
            .Where(boost =>
                boost.CharacterId == characterId &&
                ToUtcOffset(boost.ActivatedAt) <= readAt)
            .Select(boost => new
            {
                Boost = boost,
                RemainingTicks = GetRemainingOnlineTicks(boost)
            })
            .Where(row =>
                !row.RemainingTicks.HasValue ||
                row.RemainingTicks.Value > 0)
            .OrderBy(row => row.Boost.Kind)
            .ThenBy(row => row.Boost.StatusId)
            .Select(row => new CharacterProgressionBoostSnapshot(
                row.Boost.StatusId,
                row.Boost.Kind,
                row.Boost.BonusBasisPoints,
                row.Boost.Priority,
                ToUtcOffset(row.Boost.ActivatedAt),
                row.RemainingTicks,
                row.Boost.Source ?? string.Empty))
            .ToImmutableArray();

    private static long? GetRemainingOnlineTicks(
        CharacterExperienceBoost boost)
    {
        if (boost.RemainingOnlineTicks.HasValue)
        {
            return boost.RemainingOnlineTicks.Value;
        }

        return boost.ExpiresAt.HasValue
            ? Math.Max(
                0L,
                (boost.ExpiresAt.Value - boost.ActivatedAt).Ticks)
            : null;
    }

    private static DateTimeOffset ToUtcOffset(DateTime value)
    {
        if (value == default)
        {
            return default;
        }

        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return new DateTimeOffset(utc);
    }

    private static DateTimeOffset ToUtcOffset(DateTimeOffset value) =>
        value == default ? default : value.ToUniversalTime();

    private static DateTimeOffset? ToUtcOffset(DateTimeOffset? value) =>
        value.HasValue ? ToUtcOffset(value.Value) : null;
}
