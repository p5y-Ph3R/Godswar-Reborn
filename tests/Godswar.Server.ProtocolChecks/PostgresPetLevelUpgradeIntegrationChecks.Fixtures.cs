using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetLevelUpgradeIntegrationChecks
{
    private static async Task<PetLevelFixture> CreateFixtureAsync(
        PostgresGameStore store,
        string connectionString,
        string token,
        string username)
    {
        var account = await store.LoginOrCreateAccountAsync(
            username,
            string.Empty);
        int? ownerCharacterId = null;
        int? otherCharacterId = null;
        try
        {
            var owner = await store.CreateCharacterAsync(
                account.Id,
                NewCharacter($"PetLevel{token}"));
            ownerCharacterId = owner.Id;
            // Production creation is single-slot. This legacy-corruption row
            // exists only to keep the pet ownership rejection independent.
            otherCharacterId = await InsertLegacyAdditionalCharacterAsync(
                connectionString,
                account.Id,
                $"PetOther{token}");

            await using var connection =
                new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var transaction =
                await connection.BeginTransactionAsync();
            var successPetId = await InsertPetAsync(
                connection,
                transaction,
                owner.Id,
                $"Success{token}",
                level: 1,
                experience: 2_000,
                activityState: "owned",
                revision: 7);
            var insufficientPetId = await InsertPetAsync(
                connection,
                transaction,
                owner.Id,
                $"LowExp{token}",
                level: 2,
                experience: 4_499,
                activityState: "owned",
                revision: 11);
            var maximumPetId = await InsertPetAsync(
                connection,
                transaction,
                owner.Id,
                $"Maximum{token}",
                level: 120,
                experience: 10_000,
                activityState: "owned",
                revision: 5);
            var unavailablePetId = await InsertPetAsync(
                connection,
                transaction,
                owner.Id,
                $"Sealed{token}",
                level: 10,
                experience: 1_000_000,
                activityState: "sealed",
                revision: 9);
            var racePetId = await InsertPetAsync(
                connection,
                transaction,
                owner.Id,
                $"Race{token}",
                level: 1,
                experience: 1_500,
                activityState: "owned",
                revision: 21);
            var malformedPetId = await InsertPetAsync(
                connection,
                transaction,
                owner.Id,
                $"Malformed{token}",
                level: 1,
                experience: 1_500,
                activityState: "owned",
                revision: 31);
            await MakePetStatsMalformedAsync(
                connection,
                transaction,
                malformedPetId);
            var foreignPetId = await InsertPetAsync(
                connection,
                transaction,
                otherCharacterId.Value,
                $"Foreign{token}",
                level: 1,
                experience: 1_500,
                activityState: "owned",
                revision: 3);
            await transaction.CommitAsync();

            return new PetLevelFixture(
                account.Id,
                owner.Id,
                otherCharacterId.Value,
                successPetId,
                insufficientPetId,
                maximumPetId,
                unavailablePetId,
                racePetId,
                malformedPetId,
                foreignPetId);
        }
        catch
        {
            await DeletePartialFixtureAsync(
                connectionString,
                account.Id,
                username,
                ownerCharacterId,
                otherCharacterId);
            throw;
        }
    }

    private static GameCharacter NewCharacter(string name) => new()
    {
        Name = name,
        Camp = GameDefaults.SpartaCamp,
        Profession = 0,
        Level = 80
    };

    private static async Task<int> InsertLegacyAdditionalCharacterAsync(
        string connectionString,
        int accountId,
        string name)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO character_base (account_id, name)
            VALUES (@accountId, @name)
            RETURNING id;
            """,
            connection);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("name", name);
        return (int)(await command.ExecuteScalarAsync()
                     ?? throw new InvalidOperationException(
                         "Legacy pet-owner fixture returned no character ID."));
    }

    private static async Task<long> InsertPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        string name,
        short level,
        long experience,
        string activityState,
        long revision)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_pets (
                user_id,
                species_id,
                name,
                sex,
                level,
                experience,
                aptitude,
                current_energy,
                maximum_energy,
                amity,
                satiety,
                remaining_lifetime,
                activity_state,
                revision,
                rarity_added_savvy_baseline_total,
                rarity_added_savvy_policy_version,
                initial_savvy_source_version
            )
            VALUES (
                @characterId,
                1,
                @name,
                0,
                @level,
                @experience,
                1,
                100,
                100,
                100,
                100,
                600,
                @activityState,
                @revision,
                621,
                'project-v2',
                'growth-x1-v1'
            )
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("level", level);
        command.Parameters.AddWithValue("experience", experience);
        command.Parameters.AddWithValue(
            "activityState",
            activityState);
        command.Parameters.AddWithValue("revision", revision);
        var petId =
            (long)(await command.ExecuteScalarAsync()
                   ?? throw new InvalidOperationException(
                       "Pet level fixture insert returned no ID."));
        await InsertPetStatsAsync(
            connection,
            transaction,
            petId);
        return petId;
    }

    private static async Task<PetLevelState> ReadPetLevelAsync(
        string connectionString,
        long petId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT level, experience, activity_state, revision
            FROM public.character_pets
            WHERE id = @petId;
            """,
            connection);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                $"Pet level fixture {petId} disappeared.");
        }

        return new PetLevelState(
            reader.GetInt16(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetInt64(3));
    }

    private static async Task DeleteFixtureAsync(
        string connectionString,
        PetLevelFixture fixture,
        string username)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        await AssertFixtureOwnershipAsync(
            connection,
            transaction,
            fixture,
            username);
        await using (var deleteAudit = new NpgsqlCommand(
            """
            DELETE FROM public.pet_operation_audit
            WHERE user_id_snapshot IN (
                @ownerCharacterId,
                @otherCharacterId
            );
            """,
            connection,
            transaction))
        {
            deleteAudit.Parameters.AddWithValue(
                "ownerCharacterId",
                fixture.OwnerCharacterId);
            deleteAudit.Parameters.AddWithValue(
                "otherCharacterId",
                fixture.OtherCharacterId);
            await deleteAudit.ExecuteNonQueryAsync();
        }

        await using (var deleteAccount = new NpgsqlCommand(
            """
            DELETE FROM public.accounts
            WHERE id = @accountId
              AND username = @username;
            """,
            connection,
            transaction))
        {
            deleteAccount.Parameters.AddWithValue(
                "accountId",
                fixture.AccountId);
            deleteAccount.Parameters.AddWithValue(
                "username",
                username);
            Check.Equal(
                1,
                await deleteAccount.ExecuteNonQueryAsync(),
                "pet level fixture account cleanup is exact");
        }

        await transaction.CommitAsync();
    }

    private static async Task DeletePartialFixtureAsync(
        string connectionString,
        int accountId,
        string username,
        int? ownerCharacterId,
        int? otherCharacterId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        await using (var verify = new NpgsqlCommand(
            """
            SELECT id
            FROM public.accounts
            WHERE id = @accountId
              AND username = @username
            FOR UPDATE;
            """,
            connection,
            transaction))
        {
            verify.Parameters.AddWithValue("accountId", accountId);
            verify.Parameters.AddWithValue("username", username);
            Check.Equal(
                accountId,
                (int)(await verify.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Partial pet-level fixture account disappeared.")),
                "partial pet-level fixture account remains exact");
        }

        var knownCharacterIds = new[]
            {
                ownerCharacterId,
                otherCharacterId
            }
            .Where(static id => id.HasValue)
            .Select(static id => id!.Value)
            .ToArray();
        if (knownCharacterIds.Length > 0)
        {
            await using var verifyCharacters = new NpgsqlCommand(
                """
                SELECT count(*)
                FROM public.character_base
                WHERE account_id = @accountId
                  AND id = ANY(@characterIds);
                """,
                connection,
                transaction);
            verifyCharacters.Parameters.AddWithValue(
                "accountId",
                accountId);
            verifyCharacters.Parameters.AddWithValue(
                "characterIds",
                knownCharacterIds);
            Check.Equal(
                (long)knownCharacterIds.Length,
                (long)(await verifyCharacters.ExecuteScalarAsync()
                       ?? throw new InvalidOperationException(
                           "Partial character ownership returned null.")),
                "partial fixture owns every created character");
        }

        await using var delete = new NpgsqlCommand(
            """
            DELETE FROM public.accounts
            WHERE id = @accountId
              AND username = @username;
            """,
            connection,
            transaction);
        delete.Parameters.AddWithValue("accountId", accountId);
        delete.Parameters.AddWithValue("username", username);
        Check.Equal(
            1,
            await delete.ExecuteNonQueryAsync(),
            "partial pet-level fixture cleanup is exact");
        await transaction.CommitAsync();
    }

    private static async Task AssertFixtureOwnershipAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PetLevelFixture fixture,
        string username)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT character.id
            FROM public.accounts account
            INNER JOIN public.character_base character
                ON character.account_id = account.id
            WHERE account.id = @accountId
              AND account.username = @username
              AND character.id IN (
                  @ownerCharacterId,
                  @otherCharacterId
              )
            FOR UPDATE OF account, character;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "accountId",
            fixture.AccountId);
        command.Parameters.AddWithValue("username", username);
        command.Parameters.AddWithValue(
            "ownerCharacterId",
            fixture.OwnerCharacterId);
        command.Parameters.AddWithValue(
            "otherCharacterId",
            fixture.OtherCharacterId);
        var characterIds = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            characterIds.Add(reader.GetInt32(0));
        }

        Check.Equal(
            2,
            characterIds.Count,
            "pet level fixture owns exactly two characters");
        Check.True(
            characterIds.Contains(fixture.OwnerCharacterId) &&
            characterIds.Contains(fixture.OtherCharacterId),
            "pet level fixture ownership matches both exact characters");
    }

    private sealed record PetLevelFixture(
        int AccountId,
        int OwnerCharacterId,
        int OtherCharacterId,
        long SuccessPetId,
        long InsufficientPetId,
        long MaximumPetId,
        long UnavailablePetId,
        long RacePetId,
        long MalformedPetId,
        long ForeignPetId);

    private sealed record PetLevelState(
        short Level,
        long Experience,
        string ActivityState,
        long Revision);
}
