using Godswar.Server.Application.Rewards;
using Godswar.Server.Infrastructure.Rewards;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMonsterDeathRewardIntegrationChecks
{
    private static async Task AssertLevelSealingAsync(
        string connectionString)
    {
        await AssertFighterExperienceUInt32StorageAsync(
            connectionString);
        await AssertDatabaseSealConstraintAsync(connectionString);
        await AssertDurableSealedThresholdAndReplayAsync(
            connectionString);
        await AssertDurableSealedSaturationAsync(connectionString);
        await AssertLegacySealedProgressionAsync(connectionString);
    }

    private static async Task AssertFighterExperienceUInt32StorageAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "uint32_exp");
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var type = new NpgsqlCommand(
            """
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'character_base'
              AND column_name = 'fighter_job_exp';
            """,
            connection))
        {
            Check.Equal(
                "bigint",
                Convert.ToString(await type.ExecuteScalarAsync()) ??
                    string.Empty,
                "fighter EXP uses PostgreSQL bigint storage");
        }

        await using (var maximum = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET fighter_job_exp = 4294967295
            WHERE id = @characterId;
            """,
            connection))
        {
            maximum.Parameters.AddWithValue(
                "characterId",
                fixture.CharacterId);
            Check.Equal(
                1,
                await maximum.ExecuteNonQueryAsync(),
                "the complete UInt32 maximum is accepted");
        }

        await AssertExperienceBoundRejectedAsync(
            connection,
            fixture.CharacterId,
            4_294_967_296L,
            "value above UInt32 maximum");
        await AssertExperienceBoundRejectedAsync(
            connection,
            fixture.CharacterId,
            -1L,
            "negative value");
    }

    private static async Task AssertExperienceBoundRejectedAsync(
        NpgsqlConnection connection,
        int characterId,
        long experience,
        string scenario)
    {
        var rejected = false;
        try
        {
            await using var command = new NpgsqlCommand(
                """
                UPDATE public.character_base
                SET fighter_job_exp = @experience
                WHERE id = @characterId;
                """,
                connection);
            command.Parameters.AddWithValue("experience", experience);
            command.Parameters.AddWithValue("characterId", characterId);
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
            when (exception.SqlState ==
                  PostgresErrorCodes.CheckViolation)
        {
            rejected = true;
        }
        Check.True(rejected, $"PostgreSQL rejects {scenario}");
    }

    private static async Task AssertDatabaseSealConstraintAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "seal_constraint");
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var rejected = false;
        try
        {
            await using var invalid = new NpgsqlCommand(
                """
                UPDATE public.character_base
                SET fighter_level_sealed = true
                WHERE id = @characterId;
                """,
                connection);
            invalid.Parameters.AddWithValue(
                "characterId",
                fixture.CharacterId);
            await invalid.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
            when (exception.SqlState ==
                  PostgresErrorCodes.CheckViolation)
        {
            rejected = true;
        }
        Check.True(
            rejected,
            "PostgreSQL rejects sealing the default level-80 fixture");

        await using var verify = new NpgsqlCommand(
            """
            SELECT fighter_level_sealed
            FROM public.character_base
            WHERE id = @characterId;
            """,
            connection);
        verify.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        Check.Equal(
            false,
            Convert.ToBoolean(await verify.ExecuteScalarAsync()),
            "failed constraint write leaves the durable seal disabled");
    }

    private static async Task AssertDurableSealedThresholdAndReplayAsync(
        string connectionString)
    {
        var threshold = PlayerExperienceCatalog
            .GetNextLevelExperience(89);
        var fixture = await CreateSealedFixtureAsync(
            connectionString,
            "seal_threshold",
            threshold - 10,
            talentExperience: 95,
            talentPoints: 7);
        var command = CreateCommand(
            Guid.NewGuid(),
            experience: 20,
            talentExperience: 10);
        var envelope = CreateEnvelope(fixture, command);

        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(source);
        var committed = await executor.ExecuteAsync(envelope);
        var receipt = committed.Receipt ??
            throw new InvalidOperationException(
                "The sealed reward returned no receipt.");
        Check.True(
            committed.Disposition ==
                MonsterDeathRewardExecutionDisposition.Committed &&
            receipt.PreviousLevel == 89 &&
            receipt.CurrentLevel == 89 &&
            receipt.PreviousExperience == threshold - 10 &&
            receipt.CurrentExperience == threshold + 10 &&
            receipt.ExperienceGained == 20 &&
            receipt.LevelUps.Count == 0 &&
            receipt.CurrentTalentExperience == 5 &&
            receipt.TalentPointsGained == 1 &&
            receipt.CurrentTalentPoints == 8,
            "sealed durable reward crosses the normal threshold without a level-up and preserves Talent progression");

        var replay = await executor.ExecuteAsync(envelope);
        Check.True(
            replay.Disposition ==
                MonsterDeathRewardExecutionDisposition.Duplicate &&
            replay.Receipt?.ExperienceGained == 20 &&
            replay.Receipt.CurrentLevel == 89 &&
            replay.Receipt.LevelUps.Count == 0 &&
            replay.Receipt.ProgressionRevision ==
                receipt.ProgressionRevision,
            "sealed durable retry returns the original credited reward and no level-up evidence");

        var state = await ReadSealedStateAsync(
            connectionString,
            fixture.CharacterId);
        Check.True(
            state.Level == 89 &&
            state.Experience == threshold + 10 &&
            state.LevelSealed &&
            state.TalentExperience == 5 &&
            state.TalentPoints == 8 &&
            state.ProgressionRevision == 1,
            "sealed durable replay does not grant progression twice");
    }

    private static async Task AssertDurableSealedSaturationAsync(
        string connectionString)
    {
        var fixture = await CreateSealedFixtureAsync(
            connectionString,
            "seal_saturation",
            4_294_967_290L,
            talentExperience: 0,
            talentPoints: 0);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(source);

        var partial = await executor.ExecuteAsync(CreateEnvelope(
            fixture,
            CreateCommand(
                Guid.NewGuid(),
                experience: 20,
                talentExperience: 0)));
        Check.True(
            partial.Receipt?.ExperienceGained == 5 &&
            partial.Receipt.CurrentExperience == 4_294_967_295L &&
            partial.Receipt.CurrentLevel == 89 &&
            partial.Receipt.LevelUps.Count == 0,
            "sealed durable reward reports only the five EXP actually credited at saturation");

        var saturated = await executor.ExecuteAsync(CreateEnvelope(
            fixture,
            CreateCommand(
                Guid.NewGuid(),
                experience: 20,
                talentExperience: 0)));
        Check.True(
            saturated.Receipt?.ExperienceGained == 0 &&
            saturated.Receipt.CurrentExperience == 4_294_967_295L &&
            saturated.Receipt.CurrentLevel == 89 &&
            saturated.Receipt.LevelUps.Count == 0,
            "fully saturated sealed fighter receives zero credited EXP without overflow or sentinel state");

        var state = await ReadSealedStateAsync(
            connectionString,
            fixture.CharacterId);
        Check.True(
            state.Experience == 4_294_967_295L &&
            state.ProgressionRevision == 2,
            "both distinct durable deaths settle while stored EXP remains at the UInt32 ceiling");
    }

    private static async Task AssertLegacySealedProgressionAsync(
        string connectionString)
    {
        var threshold = PlayerExperienceCatalog
            .GetNextLevelExperience(89);
        var fixture = await CreateSealedFixtureAsync(
            connectionString,
            "legacy_sealed",
            threshold - 2,
            talentExperience: 99,
            talentPoints: 2);
        await using var store = new PostgresGameStore(connectionString);
        var result = await store.ApplyMonsterKillRewardAsync(
            fixture.AccountId,
            fixture.CharacterId,
            experience: 10,
            talentExperience: 2) ??
            throw new InvalidOperationException(
                "The legacy sealed reward returned no projection.");
        Check.True(
            result.PreviousLevel == 89 &&
            result.CurrentLevel == 89 &&
            result.CurrentExperience == threshold + 8 &&
            result.ExperienceGained == 10 &&
            result.LevelUps.Count == 0 &&
            result.CurrentTalentExperience == 1 &&
            result.TalentPointsGained == 1 &&
            result.CurrentTalentPoints == 3,
            "legacy PostgreSQL reward honors the same seal while preserving Talent progression");
    }

    private static async Task<RewardFixture> CreateSealedFixtureAsync(
        string connectionString,
        string scenario,
        long experience,
        int talentExperience,
        int talentPoints)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            scenario);
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET fighter_job_lv = 89,
                fighter_job_exp = @experience,
                fighter_level_sealed = true,
                "SkillExp" = @talentExperience,
                "SkillPoint" = @talentPoints
            WHERE id = @characterId;
            """,
            connection);
        command.Parameters.AddWithValue("experience", experience);
        command.Parameters.AddWithValue(
            "talentExperience",
            talentExperience);
        command.Parameters.AddWithValue("talentPoints", talentPoints);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"{scenario} sealed fixture update");
        return fixture;
    }

    private static async Task<SealedCharacterState> ReadSealedStateAsync(
        string connectionString,
        int characterId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT fighter_job_lv,
                   fighter_job_exp,
                   fighter_level_sealed,
                   "SkillExp",
                   "SkillPoint",
                   progression_reward_revision
            FROM public.character_base
            WHERE id = @characterId;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The sealed reward fixture is missing.");
        }
        return new SealedCharacterState(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetBoolean(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt64(5));
    }

    private readonly record struct SealedCharacterState(
        int Level,
        long Experience,
        bool LevelSealed,
        int TalentExperience,
        int TalentPoints,
        long ProgressionRevision);
}
