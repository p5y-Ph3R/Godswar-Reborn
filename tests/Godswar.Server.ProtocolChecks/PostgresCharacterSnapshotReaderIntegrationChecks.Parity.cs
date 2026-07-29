using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresCharacterSnapshotReaderIntegrationChecks
{
    private static async Task AssertLegacyParityAsync(
        string connectionString,
        PostgresGameStore store,
        NpgsqlDataSource dataSource,
        ICollection<SnapshotFixture> fixtures,
        string token)
    {
        var fixture = await CreateRichFixtureAsync(
            store,
            dataSource,
            $"snap_parity_{token}",
            $"SnapParity{token}");
        fixtures.Add(fixture);

        var legacyCharacter =
            await store.GetFirstCharacterAsync(fixture.AccountId) ??
            throw new InvalidOperationException(
                "Legacy character reader returned no parity fixture.");
        var legacyStats =
            await store.GetCharacterStatsAsync(
                fixture.AccountId,
                fixture.CharacterId) ??
            throw new InvalidOperationException(
                "Legacy stats reader returned no parity fixture.");
        var legacySkills = await store.GetSkillStatesAsync(
            fixture.AccountId,
            fixture.CharacterId);
        var legacyTalents = await store.GetTalentStatesAsync(
            fixture.AccountId,
            fixture.CharacterId);
        var legacyPets = await store.GetOwnedPetsAsync(
            fixture.AccountId,
            fixture.CharacterId);

        await using var reader =
            new PostgresCharacterSnapshotReader(connectionString);
        var accountSnapshot =
            await reader.ReadAsync(fixture.AccountId);
        var snapshot = accountSnapshot.Character ??
            throw new InvalidOperationException(
                "B06 reader returned an empty parity fixture.");

        AssertCoreParity(legacyCharacter, snapshot);
        AssertStatsParity(legacyStats, snapshot.CalculatedStats);
        AssertSkillParity(legacySkills, snapshot.Skills);
        AssertTalentParity(legacyTalents, snapshot.Talents);
        AssertPetParity(legacyPets, snapshot.Pets);
        AssertPersonalBoost(snapshot);

        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(accountSnapshot) ??
            throw new InvalidOperationException(
                "B06 hydrator returned an empty parity fixture.");
        Check.Equal(
            legacyStats.MaxHp,
            hydrated.Character.MaxHp,
            "hydration uses the same calculated maximum HP");
        Check.Equal(
            legacyStats.CurrentHp,
            hydrated.Character.CurrentHp,
            "hydration uses the same calculated current HP");
        Check.Equal(
            legacyCharacter.VitalsRevision,
            hydrated.Character.VitalsRevision,
            "hydration does not invent a persistence revision");
        Check.Equal(
            legacyPets.Count,
            hydrated.Pets.Count,
            "hydration preserves the complete pet count");
    }

    private static void AssertCoreParity(
        GameCharacter expected,
        CharacterLoadSnapshot actual)
    {
        Check.Equal(
            expected.Id,
            actual.Identity.CharacterId,
            "snapshot character ID matches the legacy reader");
        Check.Equal(
            expected.AccountId,
            actual.Identity.AccountId,
            "snapshot account ID matches the legacy reader");
        Check.Equal(
            expected.Name,
            actual.Identity.Name,
            "snapshot name matches the legacy reader");
        Check.Equal(
            expected.CreatedUtc,
            actual.Identity.CreatedAtUtc.UtcDateTime,
            "snapshot creation time matches the legacy reader");
        Check.Equal(
            expected.Gender,
            actual.Appearance.Gender,
            "snapshot gender matches the legacy reader");
        Check.Equal(
            expected.Camp,
            actual.Appearance.Camp,
            "snapshot camp matches the legacy reader");
        Check.Equal(
            expected.Profession,
            actual.Appearance.Profession,
            "snapshot profession matches the legacy reader");
        Check.Equal(
            expected.Hair,
            actual.Appearance.Hair,
            "snapshot hair matches the legacy reader");
        Check.Equal(
            expected.Face,
            actual.Appearance.Face,
            "snapshot face matches the legacy reader");
        Check.Equal(
            expected.Faith,
            actual.Appearance.Faith,
            "snapshot faith matches the legacy reader");
        Check.Equal(
            expected.CurrentMap,
            actual.Location.CurrentMap,
            "snapshot map matches the legacy reader");
        Check.Equal(
            expected.PositionX,
            actual.Location.PositionX,
            "snapshot X position matches the legacy reader");
        Check.Equal(
            expected.PositionZ,
            actual.Location.PositionZ,
            "snapshot Z position matches the legacy reader");
        Check.Equal(
            expected.Level,
            actual.Progression.Level,
            "snapshot level matches the legacy reader");
        Check.Equal(
            expected.Experience,
            actual.Progression.Experience,
            "snapshot experience matches the legacy reader");
        Check.Equal(
            expected.TalentPoints,
            actual.Progression.TalentPoints,
            "snapshot talent points match the legacy reader");
        Check.Equal(
            expected.TalentExperience,
            actual.Progression.TalentExperience,
            "snapshot talent experience matches the legacy reader");
        Check.Equal(
            expected.HolySuitPoints,
            actual.Progression.HolySuitPoints,
            "snapshot holy-suit points match the legacy reader");
        Check.Equal(
            expected.MaxHp,
            actual.Vitals.BaseMaxHp,
            "snapshot base maximum HP matches the legacy reader");
        Check.Equal(
            expected.MaxMp,
            actual.Vitals.BaseMaxMp,
            "snapshot base maximum MP matches the legacy reader");
        Check.Equal(
            expected.CurrentHp,
            actual.Vitals.PersistedCurrentHp,
            "snapshot persisted HP matches the legacy reader");
        Check.Equal(
            expected.CurrentMp,
            actual.Vitals.PersistedCurrentMp,
            "snapshot persisted MP matches the legacy reader");
        Check.Equal(
            expected.VitalsRevision,
            actual.Vitals.Revision,
            "snapshot vitals revision matches the legacy reader");
        Check.Equal(
            expected.Silver,
            actual.Wallet.Silver,
            "snapshot silver matches the legacy reader");
        Check.Equal(
            expected.Gold,
            actual.Wallet.Gold,
            "snapshot gold matches the legacy reader");
        Check.Equal(
            expected.Equipment,
            actual.Loadout.Equipment,
            "snapshot equipment projection matches the legacy reader");
        Check.Equal(
            expected.KitBag,
            actual.Loadout.KitBag,
            "snapshot bag projection matches the legacy reader");
        Check.Equal(
            expected.WeaponRank,
            actual.Loadout.WeaponRank,
            "snapshot weapon rank matches the legacy reader");
        Check.Equal(
            expected.ArmorRank,
            actual.Loadout.ArmorRank,
            "snapshot armor rank matches the legacy reader");
        Check.True(
            expected.ZodiacSkillGridLevels.SequenceEqual(
                actual.Zodiac.SkillGridLevels),
            "snapshot zodiac grid levels match the legacy reader");
        Check.True(
            expected.ZodiacSkillGridSkillIds.SequenceEqual(
                actual.Zodiac.SkillGridSkillIds),
            "snapshot zodiac grid skills match the legacy reader");
    }

    private static void AssertStatsParity(
        CharacterStats expected,
        CharacterCalculatedStatsSnapshot actual)
    {
        Check.Equal(
            expected.CharacterId,
            actual.CharacterId,
            "snapshot calculated-stat character ID");
        Check.Equal(expected.MaxHp, actual.MaxHp, "snapshot maximum HP");
        Check.Equal(expected.MaxMp, actual.MaxMp, "snapshot maximum MP");
        Check.Equal(expected.CurrentHp, actual.CurrentHp, "snapshot current HP");
        Check.Equal(expected.CurrentMp, actual.CurrentMp, "snapshot current MP");
        Check.Equal(
            expected.PhysicalAttack,
            actual.PhysicalAttack,
            "snapshot physical attack");
        Check.Equal(
            expected.PhysicalDefense,
            actual.PhysicalDefense,
            "snapshot physical defense");
        Check.Equal(
            expected.MagicAttack,
            actual.MagicAttack,
            "snapshot magic attack");
        Check.Equal(
            expected.MagicDefense,
            actual.MagicDefense,
            "snapshot magic defense");
        Check.Equal(expected.Hit, actual.Hit, "snapshot hit");
        Check.Equal(expected.Dodge, actual.Dodge, "snapshot dodge");
        Check.Equal(
            expected.DamageAbsorb,
            actual.DamageAbsorb,
            "snapshot damage absorb");
        Check.Equal(
            expected.WeaponScore,
            actual.WeaponScore,
            "snapshot weapon score");
        Check.Equal(
            expected.WeaponRank,
            actual.WeaponRank,
            "snapshot calculated weapon rank");
        Check.Equal(
            expected.WeaponAuraEffect,
            actual.WeaponAuraEffect,
            "snapshot calculated weapon aura");
        Check.Equal(
            expected.ArmorScore,
            actual.ArmorScore,
            "snapshot armor score");
        Check.Equal(
            expected.ArmorRank,
            actual.ArmorRank,
            "snapshot calculated armor rank");
        Check.Equal(
            expected.ArmorAuraEffect,
            actual.ArmorAuraEffect,
            "snapshot calculated armor aura");
        Check.Equal(
            expected.LearnedSkillCount,
            actual.LearnedSkillCount,
            "snapshot learned-skill count");
    }

    private static void AssertSkillParity(
        IReadOnlyList<SkillState> expected,
        IReadOnlyList<CharacterSkillSnapshot> actual)
    {
        Check.Equal(
            expected.Count,
            actual.Count,
            "snapshot skill count matches the legacy reader");
        for (var index = 0; index < expected.Count; index++)
        {
            Check.Equal(
                expected[index].SkillId,
                actual[index].SkillId,
                $"snapshot skill {index} identity");
            Check.Equal(
                expected[index].Level,
                actual[index].Level,
                $"snapshot skill {index} level");
        }
    }

    private static void AssertTalentParity(
        IReadOnlyList<TalentState> expected,
        IReadOnlyList<CharacterTalentSnapshot> actual)
    {
        Check.Equal(
            expected.Count,
            actual.Count,
            "snapshot talent count matches the legacy reader");
        for (var index = 0; index < expected.Count; index++)
        {
            Check.Equal(
                expected[index].TalentId,
                actual[index].TalentId,
                $"snapshot talent {index} identity");
            Check.Equal(
                expected[index].Rank,
                actual[index].Rank,
                $"snapshot talent {index} rank");
            Check.Equal(
                expected[index].DisplayValue,
                actual[index].DisplayValue,
                $"snapshot talent {index} display value");
            Check.Equal(
                expected[index].NextCost,
                actual[index].NextCost,
                $"snapshot talent {index} next cost");
        }
    }

    private static void AssertPetParity(
        IReadOnlyList<PetBootstrapSnapshot> expected,
        IReadOnlyList<CharacterPetSnapshot> actual)
    {
        Check.Equal(
            expected.Count,
            actual.Count,
            "snapshot pet count matches the legacy reader");
        Check.Equal(1, actual.Count, "rich fixture owns one pet");
        var legacy = expected[0];
        var snapshot = actual[0];
        Check.Equal(legacy.PetId, snapshot.PetId, "snapshot pet ID");
        Check.Equal(legacy.Name, snapshot.Name, "snapshot pet name");
        Check.Equal(legacy.Level, snapshot.Level, "snapshot pet level");
        Check.Equal(
            legacy.Experience,
            snapshot.Experience,
            "snapshot pet experience");
        Check.Equal(
            (short)legacy.Aptitude,
            snapshot.Aptitude,
            "snapshot pet aptitude");
        Check.Equal(legacy.Rank, snapshot.Rank, "snapshot pet rank");
        Check.Equal(
            legacy.Revision,
            snapshot.Revision,
            "snapshot pet revision");
        Check.Equal(
            legacy.StatValues.Count,
            snapshot.StatValues.Length,
            "snapshot pet stat count");
        Check.Equal(
            legacy.CharacterBonuses.Count,
            snapshot.CharacterBonuses.Length,
            "snapshot pet bonus count");
        Check.Equal(
            legacy.Skills.Count,
            snapshot.Skills.Length,
            "snapshot pet skill count");
        Check.Equal(
            legacy.StatValues[0].AddedSavvy,
            snapshot.StatValues[0].AddedSavvy,
            "snapshot first pet stat value");
        Check.Equal(
            legacy.CharacterBonuses[0].EffectCode,
            snapshot.CharacterBonuses[0].EffectCode,
            "snapshot accepts the schema-valid zero pet effect code");
        Check.Equal(
            legacy.Skills[0].SkillId,
            snapshot.Skills[0].SkillId,
            "snapshot first pet skill");
    }

    private static void AssertPersonalBoost(CharacterLoadSnapshot snapshot)
    {
        Check.Equal(
            1,
            snapshot.PersonalBoosts.Length,
            "snapshot reads only the raw personal boost fixture");
        var boost = snapshot.PersonalBoosts[0];
        Check.Equal(1501, boost.StatusId, "snapshot boost status");
        Check.Equal(1001, boost.Kind, "snapshot boost kind");
        Check.Equal(
            1_000,
            boost.BonusBasisPoints,
            "snapshot boost basis points");
        Check.Equal(
            36_000_000_000L,
            boost.RemainingOnlineTicks ?? -1,
            "snapshot preserves online-duration boost time");
    }
}
