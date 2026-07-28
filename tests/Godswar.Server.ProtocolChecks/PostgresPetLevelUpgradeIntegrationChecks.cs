using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetLevelUpgradeIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet level-up integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"pet_level_{token}";
        PetLevelFixture? fixture = null;

        try
        {
            await using var storeA =
                new PostgresGameStore(connectionString);
            await using var storeB =
                new PostgresGameStore(connectionString);
            await storeA.EnsureSeedDataAsync();

            fixture = await CreateFixtureAsync(
                storeA,
                connectionString,
                token,
                username);

            await AssertOwnershipRejectionsAsync(storeA, fixture);
            await AssertStateRejectionsAsync(
                storeA,
                connectionString,
                fixture);
            await AssertMalformedStatsRollbackAsync(
                storeA,
                connectionString,
                fixture);
            await AssertOneLevelCommitAsync(
                storeA,
                connectionString,
                fixture);
            await AssertConcurrentDuplicateAsync(
                storeA,
                storeB,
                connectionString,
                fixture);
        }
        finally
        {
            if (fixture is not null)
            {
                await DeleteFixtureAsync(
                    connectionString,
                    fixture,
                    username);
            }
        }
    }

    private static async Task AssertOwnershipRejectionsAsync(
        PostgresGameStore store,
        PetLevelFixture fixture)
    {
        var wrongAccount = await store.UpgradePetLevelAsync(
            fixture.AccountId + 1,
            fixture.OwnerCharacterId,
            fixture.SuccessPetId);
        Check.Equal(
            (int)PetLevelUpgradeStatus.CharacterNotFound,
            (int)wrongAccount.Status,
            "pet level-up binds the character to its account");

        var wrongPetOwner = await store.UpgradePetLevelAsync(
            fixture.AccountId,
            fixture.OwnerCharacterId,
            fixture.ForeignPetId);
        Check.Equal(
            (int)PetLevelUpgradeStatus.PetNotFound,
            (int)wrongPetOwner.Status,
            "pet level-up binds the pet to its character");
    }

    private static async Task AssertStateRejectionsAsync(
        PostgresGameStore store,
        string connectionString,
        PetLevelFixture fixture)
    {
        var insufficientBefore = await ReadPetLevelAsync(
            connectionString,
            fixture.InsufficientPetId);
        var insufficientStatsBefore = await ReadPetStatsAsync(
            connectionString,
            fixture.InsufficientPetId);
        var insufficient = await store.UpgradePetLevelAsync(
            fixture.AccountId,
            fixture.OwnerCharacterId,
            fixture.InsufficientPetId);
        Check.Equal(
            (int)PetLevelUpgradeStatus.InsufficientExperience,
            (int)insufficient.Status,
            "pet level-up rejects insufficient experience");
        Check.Equal(
            insufficientBefore,
            await ReadPetLevelAsync(
                connectionString,
                fixture.InsufficientPetId),
            "insufficient experience preserves level, EXP, and revision");
        Check.True(
            insufficientStatsBefore.SequenceEqual(
                await ReadPetStatsAsync(
                    connectionString,
                    fixture.InsufficientPetId)),
            "insufficient experience preserves every pet stat row");
        await AssertRejectedStatAuditAsync(
            connectionString,
            fixture,
            fixture.InsufficientPetId,
            insufficientStatsBefore);

        var maximumBefore = await ReadPetLevelAsync(
            connectionString,
            fixture.MaximumPetId);
        var maximumStatsBefore = await ReadPetStatsAsync(
            connectionString,
            fixture.MaximumPetId);
        var maximum = await store.UpgradePetLevelAsync(
            fixture.AccountId,
            fixture.OwnerCharacterId,
            fixture.MaximumPetId);
        Check.Equal(
            (int)PetLevelUpgradeStatus.MaximumLevel,
            (int)maximum.Status,
            "pet level-up rejects level 120");
        Check.Equal(
            maximumBefore,
            await ReadPetLevelAsync(
                connectionString,
                fixture.MaximumPetId),
            "maximum-level rejection preserves persistent state");
        Check.True(
            maximumStatsBefore.SequenceEqual(
                await ReadPetStatsAsync(
                    connectionString,
                    fixture.MaximumPetId)),
            "maximum-level rejection preserves every pet stat row");
        await AssertRejectedStatAuditAsync(
            connectionString,
            fixture,
            fixture.MaximumPetId,
            maximumStatsBefore);

        var unavailableBefore = await ReadPetLevelAsync(
            connectionString,
            fixture.UnavailablePetId);
        var unavailableStatsBefore = await ReadPetStatsAsync(
            connectionString,
            fixture.UnavailablePetId);
        var unavailable = await store.UpgradePetLevelAsync(
            fixture.AccountId,
            fixture.OwnerCharacterId,
            fixture.UnavailablePetId);
        Check.Equal(
            (int)PetLevelUpgradeStatus.PetUnavailable,
            (int)unavailable.Status,
            "sealed pet cannot level up");
        Check.Equal(
            unavailableBefore,
            await ReadPetLevelAsync(
                connectionString,
                fixture.UnavailablePetId),
            "unavailable-pet rejection preserves persistent state");
        Check.True(
            unavailableStatsBefore.SequenceEqual(
                await ReadPetStatsAsync(
                    connectionString,
                    fixture.UnavailablePetId)),
            "unavailable-pet rejection preserves every pet stat row");
        await AssertRejectedStatAuditAsync(
            connectionString,
            fixture,
            fixture.UnavailablePetId,
            unavailableStatsBefore);
    }

    private static async Task AssertMalformedStatsRollbackAsync(
        PostgresGameStore store,
        string connectionString,
        PetLevelFixture fixture)
    {
        var petBefore = await ReadPetLevelAsync(
            connectionString,
            fixture.MalformedPetId);
        var statsBefore = await ReadPetStatsAsync(
            connectionString,
            fixture.MalformedPetId);
        var auditsBefore = await ReadPetAuditCountAsync(
            connectionString,
            fixture.MalformedPetId);
        var rejectedMalformedState = false;
        try
        {
            await store.UpgradePetLevelAsync(
                fixture.AccountId,
                fixture.OwnerCharacterId,
                fixture.MalformedPetId);
        }
        catch (InvalidOperationException)
        {
            rejectedMalformedState = true;
        }

        Check.True(
            rejectedMalformedState,
            "malformed stat provenance rejects the whole level-up");
        Check.Equal(
            petBefore,
            await ReadPetLevelAsync(
                connectionString,
                fixture.MalformedPetId),
            "malformed stats roll back level, EXP, and pet revision");
        Check.True(
            statsBefore.SequenceEqual(
                await ReadPetStatsAsync(
                    connectionString,
                    fixture.MalformedPetId)),
            "malformed stats roll back every stat row");
        Check.Equal(
            auditsBefore,
            await ReadPetAuditCountAsync(
                connectionString,
                fixture.MalformedPetId),
            "rolled-back malformed level-up writes no misleading audit");
    }

    private static async Task AssertOneLevelCommitAsync(
        PostgresGameStore store,
        string connectionString,
        PetLevelFixture fixture)
    {
        var statsBefore = await ReadPetStatsAsync(
            connectionString,
            fixture.SuccessPetId);
        var result = await store.UpgradePetLevelAsync(
            fixture.AccountId,
            fixture.OwnerCharacterId,
            fixture.SuccessPetId);

        Check.True(result.Succeeded, "eligible pet level-up commits");
        Check.Equal((short)1, result.PreviousLevel, "previous pet level");
        Check.Equal((short)2, result.Level, "exactly one level advances");
        Check.Equal(2_000L, result.PreviousExperience, "previous pet EXP");
        Check.Equal(500L, result.Experience, "remaining pet EXP");
        Check.Equal(1_500, result.ExperienceSpent, "level-one EXP cost");
        Check.Equal(8L, result.Revision, "pet revision advances once");

        var expectedStatsAfter = AdvanceStatsOneLevel(statsBefore);
        Check.Equal(
            ToPetSavvy(expectedStatsAfter),
            result.BasicSavvy,
            "successful result returns all six post-level basic savvy values");
        Check.Equal(
            new PetLevelState(2, 500, "owned", 8),
            await ReadPetLevelAsync(
                connectionString,
                fixture.SuccessPetId),
            "one authoritative update deducts EXP and advances revision");
        var statsAfter = await ReadPetStatsAsync(
            connectionString,
            fixture.SuccessPetId);
        Check.True(
            statsAfter.SequenceEqual(expectedStatsAfter),
            "one level adds each base growth rate and advances all stat revisions once");
        Check.Equal(
            statsBefore[1].InitialSavvy + 9m,
            statsAfter[1].InitialSavvy,
            "strength growth rate nine adds exactly nine at level two");
        await AssertCommittedAuditAsync(
            connectionString,
            fixture);
        await AssertCommittedStatAuditAsync(
            connectionString,
            fixture,
            statsBefore,
            expectedStatsAfter);
    }

    private static async Task AssertConcurrentDuplicateAsync(
        PostgresGameStore storeA,
        PostgresGameStore storeB,
        string connectionString,
        PetLevelFixture fixture)
    {
        var statsBefore = await ReadPetStatsAsync(
            connectionString,
            fixture.RacePetId);
        var results = await Task.WhenAll(
            storeA.UpgradePetLevelAsync(
                fixture.AccountId,
                fixture.OwnerCharacterId,
                fixture.RacePetId),
            storeB.UpgradePetLevelAsync(
                fixture.AccountId,
                fixture.OwnerCharacterId,
                fixture.RacePetId));

        Check.Equal(
            1,
            results.Count(static result => result.Succeeded),
            "only one duplicate pet level-up commits");
        Check.Equal(
            1,
            results.Count(static result =>
                result.Status ==
                    PetLevelUpgradeStatus.InsufficientExperience),
            "serialized duplicate observes the committed EXP deduction");
        var expectedStatsAfter = AdvanceStatsOneLevel(statsBefore);
        Check.Equal(
            ToPetSavvy(expectedStatsAfter),
            results.Single(static result => result.Succeeded).BasicSavvy,
            "race winner returns the six exactly-once basic savvy totals");
        Check.Equal(
            new PetLevelState(2, 0, "owned", 22),
            await ReadPetLevelAsync(
                connectionString,
                fixture.RacePetId),
            "concurrent duplicate spends the threshold exactly once");
        Check.True(
            (await ReadPetStatsAsync(
                connectionString,
                fixture.RacePetId))
                .SequenceEqual(expectedStatsAfter),
            "concurrent duplicate advances each stat exactly once");
        await AssertRaceAuditsAsync(
            connectionString,
            fixture);
        await AssertRaceStatAuditsAsync(
            connectionString,
            fixture,
            statsBefore,
            expectedStatsAfter);
    }
}
