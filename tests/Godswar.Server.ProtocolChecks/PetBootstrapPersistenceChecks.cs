using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetBootstrapPersistenceChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        await CheckJsonFallbackAsync();

        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet bootstrap persistence " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await CheckPostgresAggregateAsync(connectionString);
    }

    private static async Task CheckJsonFallbackAsync()
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"reborn-pet-bootstrap-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(path);
        try
        {
            await using var store = new JsonGameStore(path);
            var pets = await store.GetOwnedPetsAsync(7, 13);
            Check.Equal(0, pets.Count, "JSON pet bootstrap safely returns empty");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static async Task CheckPostgresAggregateAsync(
        string connectionString)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"pet_boot_{token}";
        int? accountId = null;
        int? characterId = null;

        try
        {
            await using var store = new PostgresGameStore(connectionString);
            await store.EnsureSeedDataAsync();
            var account =
                await store.LoginOrCreateAccountAsync(username, string.Empty);
            accountId = account.Id;
            var character = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter { Name = $"Pet{token}" });
            characterId = character.Id;
            var petId = await InsertPetFixtureAsync(
                connectionString,
                character.Id);

            var pets = await store.GetOwnedPetsAsync(
                account.Id,
                character.Id);
            Check.Equal(1, pets.Count, "PostgreSQL owned-pet count");
            AssertPet(pets[0], account.Id, character.Id, petId);

            Check.Equal(
                0,
                (await store.GetOwnedPetsAsync(
                    int.MaxValue,
                    character.Id)).Count,
                "pet bootstrap enforces account ownership");
            Check.Equal(
                0,
                (await store.GetOwnedPetsAsync(
                    account.Id,
                    int.MaxValue)).Count,
                "pet bootstrap rejects a missing character");

            await CheckPresenceTransitionsAsync(
                store,
                account.Id,
                character.Id,
                petId);
            var secondPetId = await InsertAdditionalPetFixtureAsync(
                connectionString,
                character.Id);
            await CheckUnavailablePetRejectionsAsync(
                store,
                connectionString,
                account.Id,
                character.Id,
                secondPetId);
            await CheckConcurrentTakeAsync(
                store,
                account.Id,
                character.Id,
                petId,
                secondPetId);
            await CheckCarriedPetSwitchingAsync(
                store,
                account.Id,
                character.Id,
                petId,
                secondPetId);
            await CheckPresenceAuditAsync(
                connectionString,
                character.Id);
        }
        finally
        {
            if (accountId.HasValue)
            {
                await DeleteFixtureAsync(
                    connectionString,
                    accountId.Value,
                    username,
                    characterId);
            }
        }
    }

    private static async Task CheckConcurrentTakeAsync(
        PostgresGameStore store,
        int accountId,
        int characterId,
        long firstPetId,
        long secondPetId)
    {
        var results = await Task.WhenAll(
            store.TransitionPetPresenceAsync(
                accountId,
                characterId,
                firstPetId,
                PetPresenceOperation.Take),
            store.TransitionPetPresenceAsync(
                accountId,
                characterId,
                secondPetId,
                PetPresenceOperation.Take));
        Check.True(
            results.All(static result => result.Succeeded),
            "concurrent Take requests serialize successfully");
        var pets = await store.GetOwnedPetsAsync(
            accountId,
            characterId);
        Check.Equal(
            1,
            pets.Count(static pet => pet.IsCarried),
            "concurrent Take leaves exactly one carried pet");
        Check.Equal(
            0,
            pets.Count(static pet => pet.IsSummoned),
            "concurrent Take cannot implicitly summon a pet");
    }

    private static async Task CheckUnavailablePetRejectionsAsync(
        PostgresGameStore store,
        string connectionString,
        int accountId,
        int characterId,
        long petId)
    {
        var summonBeforeTake = await store.TransitionPetPresenceAsync(
            accountId,
            characterId,
            petId,
            PetPresenceOperation.CallOut);
        Check.True(
            summonBeforeTake.Status ==
            PetPresenceTransitionStatus.PetNotTaken,
            "uncarried pet cannot be summoned");

        var recallBeforeTake = await store.TransitionPetPresenceAsync(
            accountId,
            characterId,
            petId,
            PetPresenceOperation.Recall);
        Check.True(
            recallBeforeTake.Status ==
            PetPresenceTransitionStatus.PetNotTaken,
            "uncarried pet cannot be recalled");

        await SetPetActivityStateAsync(
            connectionString,
            petId,
            "sealed");
        var sealedTake = await store.TransitionPetPresenceAsync(
            accountId,
            characterId,
            petId,
            PetPresenceOperation.Take);
        Check.True(
            sealedTake.Status ==
            PetPresenceTransitionStatus.PetUnavailable,
            "sealed pet cannot be carried");
        await SetPetActivityStateAsync(
            connectionString,
            petId,
            "owned");
    }

    private static async Task CheckPresenceAuditAsync(
        string connectionString,
        int characterId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                count(*)::integer,
                count(*) FILTER (
                    WHERE outcome = 'committed'
                )::integer,
                count(*) FILTER (
                    WHERE outcome = 'rejected'
                )::integer,
                count(*) FILTER (
                    WHERE operation = 'take'
                )::integer,
                count(*) FILTER (
                    WHERE operation = 'summon'
                )::integer,
                count(*) FILTER (
                    WHERE operation = 'dismiss'
                )::integer,
                count(DISTINCT request_id)::integer,
                bool_and(
                    (outcome = 'committed'
                     AND before_state IS NOT NULL
                     AND after_state IS NOT NULL
                     AND reason_code IS NULL)
                    OR
                    (outcome = 'rejected'
                     AND reason_code IS NOT NULL)
                )
            FROM pet_operation_audit
            WHERE user_id_snapshot = @characterId;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "pet presence audit summary exists");
        Check.Equal(12, reader.GetInt32(0), "pet action audit row count");
        Check.Equal(7, reader.GetInt32(1), "committed pet action audits");
        Check.Equal(5, reader.GetInt32(2), "rejected pet action audits");
        Check.Equal(8, reader.GetInt32(3), "Take action audits");
        Check.Equal(2, reader.GetInt32(4), "summon action audits");
        Check.Equal(2, reader.GetInt32(5), "dismiss action audits");
        Check.Equal(
            12,
            reader.GetInt32(6),
            "each legacy pet action receives a server request ID");
        Check.True(
            reader.GetBoolean(7),
            "pet audits retain state for commits and reasons for rejections");
    }

    private static async Task CheckCarriedPetSwitchingAsync(
        PostgresGameStore store,
        int accountId,
        int characterId,
        long firstPetId,
        long secondPetId)
    {
        var selectHigherId = await store.TransitionPetPresenceAsync(
            accountId,
            characterId,
            secondPetId,
            PetPresenceOperation.Take);
        Check.True(
            selectHigherId.Succeeded,
            "Take can switch to a higher pet ID");
        var afterHigher = await store.GetOwnedPetsAsync(
            accountId,
            characterId);
        Check.True(
            !afterHigher.Single(pet => pet.PetId == firstPetId).IsCarried &&
            afterHigher.Single(pet => pet.PetId == secondPetId).IsCarried,
            "higher-ID Take leaves exactly one carried pet");

        var selectLowerId = await store.TransitionPetPresenceAsync(
            accountId,
            characterId,
            firstPetId,
            PetPresenceOperation.Take);
        Check.True(
            selectLowerId.Succeeded,
            "Take can switch to a lower pet ID");
        var afterLower = await store.GetOwnedPetsAsync(
            accountId,
            characterId);
        Check.True(
            afterLower.Single(pet => pet.PetId == firstPetId).IsCarried &&
            !afterLower.Single(pet => pet.PetId == secondPetId).IsCarried,
            "lower-ID Take avoids transient unique-index conflicts");
    }

    private static async Task CheckPresenceTransitionsAsync(
        PostgresGameStore store,
        int accountId,
        int characterId,
        long petId)
    {
        var wrongAccount = await store.TransitionPetPresenceAsync(
            int.MaxValue,
            characterId,
            petId,
            PetPresenceOperation.Take);
        Check.True(
            wrongAccount.Status ==
            PetPresenceTransitionStatus.CharacterNotFound,
            "pet transition enforces account ownership");

        var wrongPet = await store.TransitionPetPresenceAsync(
            accountId,
            characterId,
            long.MaxValue,
            PetPresenceOperation.Take);
        Check.True(
            wrongPet.Status ==
            PetPresenceTransitionStatus.PetNotFound,
            "pet transition rejects an unowned pet");

        var recalled = await store.TransitionPetPresenceAsync(
            accountId,
            characterId,
            petId,
            PetPresenceOperation.Recall);
        Check.True(recalled.Succeeded, "summoned pet can be recalled");
        Check.True(recalled.IsCarried, "recall retains carried state");
        Check.True(!recalled.IsSummoned, "recall clears summoned state");
        var afterRecall = (await store.GetOwnedPetsAsync(
            accountId,
            characterId)).Single();
        Check.True(afterRecall.IsCarried, "carried state persists after recall");
        Check.True(!afterRecall.IsSummoned, "recall persists immediately");
        Check.True(
            !afterRecall.ContributesToCharacter,
            "recall removes character contribution atomically");

        var calledOut = await store.TransitionPetPresenceAsync(
            accountId,
            characterId,
            petId,
            PetPresenceOperation.CallOut);
        Check.True(calledOut.Succeeded, "carried pet can be summoned");
        Check.True(calledOut.IsSummoned, "summon result is authoritative");
        Check.True(
            (await store.GetOwnedPetsAsync(
                accountId,
                characterId)).Single().IsSummoned,
            "summon persists immediately");

        var taken = await store.TransitionPetPresenceAsync(
            accountId,
            characterId,
            petId,
            PetPresenceOperation.Take);
        Check.True(taken.Succeeded, "Take is an authoritative transition");
        Check.True(taken.IsCarried, "Take selects the pet");
        Check.True(!taken.IsSummoned, "Take does not implicitly summon");
        var afterTake = (await store.GetOwnedPetsAsync(
            accountId,
            characterId)).Single();
        Check.True(afterTake.IsCarried, "Take persists carried state");
        Check.True(!afterTake.IsSummoned, "Take persists recalled state");
        Check.Equal(15L, afterTake.Revision, "pet actions advance revision");
    }

    private static void AssertPet(
        PetBootstrapSnapshot pet,
        int accountId,
        int characterId,
        long petId)
    {
        Check.Equal(petId, pet.PetId, "pet ID");
        Check.Equal(accountId, pet.AccountId, "pet account ID");
        Check.Equal(characterId, pet.OwnerCharacterId, "pet character ID");
        Check.Equal((short)37, pet.SpeciesId, "pet species");
        Check.Equal("Godly Fixture", pet.Name, "pet name");
        Check.Equal((byte)1, pet.Sex, "pet sex");
        Check.Equal((short)80, pet.Level, "pet level");
        Check.Equal(123_456_789L, pet.Experience, "pet experience");
        Check.Equal(
            (short)PetAptitude.Godly,
            (short)pet.Aptitude,
            "pet aptitude");
        Check.Equal(25.5m, pet.Rank, "pet rank");
        Check.Equal((short)3, pet.CompletedRebirths, "completed rebirths");
        Check.Equal((short)2, pet.RebirthsRemaining, "rebirths remaining");
        Check.Equal(7, pet.CompletedPetMerges, "completed pet merges");
        Check.True(pet.HasSoulContract, "pet soul contract");
        Check.True(pet.HasOwnerMergeTalent, "pet owner-merge talent");
        Check.Equal(90, pet.CurrentEnergy, "pet energy");
        Check.Equal(100, pet.MaximumEnergy, "pet maximum energy");
        Check.Equal(321, pet.Amity, "pet amity");
        Check.Equal(88, pet.Satiety, "pet satiety");
        Check.Equal(6_543, pet.RemainingLifetime, "pet remaining lifetime");
        Check.Equal(9, pet.AvailableStatPoints, "pet available stat points");
        Check.True(pet.GrowthRevealed, "pet growth reveal");
        Check.True(pet.IsBound, "pet binding");
        Check.Equal("owned", pet.ActivityState, "pet activity");
        Check.True(pet.IsCarried, "pet carried state");
        Check.True(pet.IsSummoned, "pet summoned state");
        Check.True(
            pet.ContributesToCharacter,
            "pet character contribution state");
        Check.Equal(12L, pet.Revision, "pet revision");

        Check.Equal(6, pet.StatValues.Count, "pet stat row count");
        for (short code = 1; code <= 6; code++)
        {
            var stat = pet.StatValues[code - 1];
            Check.Equal(code, stat.StatCode, $"pet stat code {code}");
            Check.Equal(code + 0.1m, stat.InitialSavvy, $"initial stat {code}");
            Check.Equal(code + 10.2m, stat.AddedSavvy, $"added stat {code}");
            Check.Equal(
                code + 30.4m,
                stat.BaseGrowthRate,
                $"base growth stat {code}");
            Check.Equal(
                code + 20.3m,
                stat.GrowthAcceleration,
                $"growth acceleration stat {code}");
            Check.Equal((long)code, stat.Revision, $"stat revision {code}");
        }

        Check.Equal(
            2,
            pet.CharacterBonuses.Count,
            "pet character-bonus row count");
        Check.Equal(
            (short)0,
            pet.CharacterBonuses[0].EffectCode,
            "first bonus code");
        Check.Equal(
            11.25m,
            pet.CharacterBonuses[0].EffectValue,
            "first bonus value");
        Check.Equal(
            41L,
            pet.CharacterBonuses[0].Revision,
            "first bonus revision");
        Check.Equal(
            (short)38,
            pet.CharacterBonuses[1].EffectCode,
            "second bonus code");

        Check.Equal(2, pet.Skills.Count, "pet skill row count");
        Check.Equal(5_001, pet.Skills[0].SkillId, "first pet skill");
        Check.Equal((short)0, pet.Skills[0].SlotIndex, "first skill slot");
        Check.Equal((short)4, pet.Skills[0].SkillRank, "first skill rank");
        Check.Equal(444, pet.Skills[0].SkillExperience, "first skill EXP");
        Check.True(pet.Skills[0].IsActive, "first skill active state");
        Check.Equal(31L, pet.Skills[0].Revision, "first skill revision");
        Check.Equal(5_002, pet.Skills[1].SkillId, "second pet skill");
        Check.Equal((short)5, pet.Skills[1].SlotIndex, "second skill slot");
        Check.True(!pet.Skills[1].IsActive, "second skill inactive state");
    }

}
