using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;
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
            new PostgresCharacterSnapshotReader(
                connectionString,
                store.ItemContent.Templates);
        ICharacterRuntimeProjectionReader runtimeProjectionReader = reader;
        IOwnedPetSnapshotReader ownedPetSnapshotReader = reader;
        var focusedStats =
            await runtimeProjectionReader.ReadCalculatedStatsAsync(
                fixture.AccountId,
                fixture.CharacterId) ??
            throw new InvalidOperationException(
                "Focused stats reader returned no parity fixture.");
        var focusedPets =
            await ownedPetSnapshotReader.ReadOwnedPetsAsync(
                fixture.AccountId,
                fixture.CharacterId);

        var accountSnapshot =
            await reader.ReadAsync(fixture.AccountId);
        var snapshot = accountSnapshot.Character ??
            throw new InvalidOperationException(
                "B06 reader returned an empty parity fixture.");

        AssertCoreParity(legacyCharacter, snapshot);
        AssertStatsParity(legacyStats, focusedStats);
        Check.Equal(
            focusedStats,
            snapshot.CalculatedStats,
            "focused stats match the consistent login snapshot");
        AssertSkillParity(legacySkills, snapshot.Skills);
        await AssertScalarSkillParityAsync(
            runtimeProjectionReader,
            fixture,
            legacySkills);
        AssertTalentParity(legacyTalents, snapshot.Talents);
        AssertPetParity(legacyPets, focusedPets);
        AssertPetParity(legacyPets, snapshot.Pets);
        await AssertFocusedOwnershipBoundaryAsync(
            runtimeProjectionReader,
            ownedPetSnapshotReader,
            fixture,
            legacySkills[0].SkillId);
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

    private static async Task AssertScalarSkillParityAsync(
        ICharacterRuntimeProjectionReader reader,
        SnapshotFixture fixture,
        IReadOnlyList<SkillState> legacySkills)
    {
        Check.True(
            legacySkills.Count > 0,
            "rich fixture exposes at least one learned skill");
        Check.True(
            legacySkills.Any(static skill => skill.SkillId == 0),
            "Warrior parity fixture preserves learned Light Chop skill ID zero");
        foreach (var skill in legacySkills)
        {
            Check.True(
                await reader.IsSkillLearnedAsync(
                    fixture.AccountId,
                    fixture.CharacterId,
                    skill.SkillId),
                $"focused scalar reader finds learned skill {skill.SkillId}");
        }

        Check.True(
            !await reader.IsSkillLearnedAsync(
                fixture.AccountId,
                fixture.CharacterId,
                int.MaxValue),
            "focused scalar reader rejects an unlearned skill");
    }

    private static async Task AssertFocusedOwnershipBoundaryAsync(
        ICharacterRuntimeProjectionReader runtimeReader,
        IOwnedPetSnapshotReader petReader,
        SnapshotFixture fixture,
        int learnedSkillId)
    {
        var otherAccountId = checked(fixture.AccountId + 1);
        Check.True(
            await runtimeReader.ReadCalculatedStatsAsync(
                otherAccountId,
                fixture.CharacterId) is null,
            "focused stats cannot cross an account ownership boundary");
        Check.True(
            !await runtimeReader.IsSkillLearnedAsync(
                otherAccountId,
                fixture.CharacterId,
                learnedSkillId),
            "focused learned-skill lookup cannot cross an account ownership boundary");
        Check.Equal(
            0,
            (await petReader.ReadOwnedPetsAsync(
                otherAccountId,
                fixture.CharacterId)).Length,
            "focused pet lookup cannot cross an account ownership boundary");
    }

    private static async Task AssertFocusedProjectionContractsAsync(
        ICharacterRuntimeProjectionReader runtimeReader,
        IOwnedPetSnapshotReader petReader)
    {
        await ExpectArgumentOutOfRangeAsync(
            () => runtimeReader.ReadCalculatedStatsAsync(0, 1),
            "focused stats reject a non-positive account ID");
        await ExpectArgumentOutOfRangeAsync(
            () => runtimeReader.IsSkillLearnedAsync(1, 1, -1),
            "focused skill lookup rejects a negative skill ID");
        await ExpectArgumentOutOfRangeAsync(
            () => petReader.ReadOwnedPetsAsync(1, 0),
            "focused pet lookup rejects a non-positive character ID");
    }

    private static async Task
        AssertFocusedProjectionContractValidationAsync()
    {
        const string unusedConnectionString =
            "Host=127.0.0.1;Port=1;Database=unused;" +
            "Username=unused;Password=unused;Timeout=1";
        await using var reader =
            new PostgresCharacterSnapshotReader(
                unusedConnectionString,
                TestItemContent.Catalog);
        await AssertFocusedProjectionContractsAsync(reader, reader);
    }

    private static async Task ExpectArgumentOutOfRangeAsync(
        Func<Task> action,
        string description)
    {
        try
        {
            await action();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected ArgumentOutOfRangeException: {description}.");
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
