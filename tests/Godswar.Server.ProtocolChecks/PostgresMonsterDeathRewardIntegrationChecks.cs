using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Rewards;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Rewards;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresMonsterDeathRewardIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL exactly-once monster reward settlement";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b(?:08|09|10|11|12)_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                "SKIP PostgreSQL monster reward integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using (var safety =
                     NpgsqlDataSource.Create(connectionString))
        {
            await using var command =
                safety.CreateCommand("SELECT current_database();");
            var databaseName =
                await command.ExecuteScalarAsync() as string ??
                string.Empty;
            if (!DisposableDatabasePattern.IsMatch(databaseName))
            {
                Console.WriteLine(
                    "SKIP PostgreSQL monster reward integration " +
                    $"requires a disposable test database; received '{databaseName}'");
                return;
            }
        }

        await using (var store =
                     new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
        }

        await AssertGlobalDeathContentionAsync(connectionString);
        await AssertZeroRewardClaimAndConflictAsync(connectionString);
    }

    private static async Task AssertGlobalDeathContentionAsync(
        string connectionString)
    {
        var fixtureA = await CreateFixtureAsync(
            connectionString,
            "racea");
        var fixtureB = await CreateFixtureAsync(
            connectionString,
            "raceb");
        var runtimeId = Guid.NewGuid();
        var command = CreateCommand(
            runtimeId,
            experience: 120,
            talentExperience: 50);
        var envelopeA = CreateEnvelope(fixtureA, command);
        var envelopeB = CreateEnvelope(fixtureB, command);

        await using var sourceA =
            NpgsqlDataSource.Create(connectionString);
        await using var sourceB =
            NpgsqlDataSource.Create(connectionString);
        var results = await Task.WhenAll(
            CreateExecutor(sourceA).ExecuteAsync(envelopeA),
            CreateExecutor(sourceB).ExecuteAsync(envelopeB));
        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                    MonsterDeathRewardExecutionDisposition.Committed),
            "one recipient globally claims a monster death");
        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                    MonsterDeathRewardExecutionDisposition
                        .RequestHashConflict),
            "the second recipient cannot settle the same death");

        var committed = results.Single(result =>
            result.Disposition ==
                MonsterDeathRewardExecutionDisposition.Committed);
        var receipt = committed.Receipt ??
            throw new InvalidOperationException(
                "The reward race winner returned no receipt.");
        Check.True(
            receipt.DeathEventId == command.DeathEventId &&
            receipt.ExperienceGained == 120 &&
            receipt.TalentExperienceGained == 50 &&
            receipt.ProgressionRevision == 1,
            "the winner returns canonical progression evidence");
        var state = await ReadRaceStateAsync(
            connectionString,
            fixtureA,
            fixtureB,
            command.DeathEventId);
        Check.True(
            state.TotalExperience == 120 &&
            state.TotalTalentExperience == 50 &&
            state.TotalRevision == 1 &&
            state.Settlements == 1 &&
            state.Inboxes == 1 &&
            state.Audits == 1 &&
            state.OutboxEvents == 1,
            "claim, progression, inbox, audit, and outbox commit once");

        var winnerEnvelope =
            receipt.CharacterId == fixtureA.CharacterId
                ? envelopeA
                : envelopeB;
        await using var replaySource =
            NpgsqlDataSource.Create(connectionString);
        var replay = await CreateExecutor(replaySource)
            .ExecuteAsync(winnerEnvelope);
        Check.True(
            replay.Disposition ==
                MonsterDeathRewardExecutionDisposition.Duplicate &&
            replay.Receipt is { } replayReceipt &&
            replayReceipt.DeathEventId == receipt.DeathEventId &&
            replayReceipt.CharacterId == receipt.CharacterId &&
            replayReceipt.ExperienceGained ==
                receipt.ExperienceGained &&
            replayReceipt.TalentExperienceGained ==
                receipt.TalentExperienceGained &&
            replayReceipt.ProgressionRevision ==
                receipt.ProgressionRevision &&
            replayReceipt.AuditReference ==
                receipt.AuditReference &&
            replayReceipt.OutboxEventId ==
                receipt.OutboxEventId &&
            replay.Projection?.Revision == 1,
            "a retry returns the original receipt without a second grant");
        var replayState = await ReadRaceStateAsync(
            connectionString,
            fixtureA,
            fixtureB,
            command.DeathEventId);
        Check.True(
            replayState.TotalExperience == 120 &&
            replayState.TotalRevision == 1 &&
            replayState.Settlements == 1 &&
            replayState.OutboxEvents == 1,
            "duplicate replay changes no authoritative reward value");
    }

    private static async Task AssertZeroRewardClaimAndConflictAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "zero");
        var runtimeId = Guid.NewGuid();
        var zero = CreateCommand(
            runtimeId,
            experience: 0,
            talentExperience: 0);
        var zeroEnvelope = CreateEnvelope(fixture, zero);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var committed = await CreateExecutor(source)
            .ExecuteAsync(zeroEnvelope);
        Check.True(
            committed.Disposition ==
                MonsterDeathRewardExecutionDisposition.Committed &&
            committed.Receipt?.ExperienceGained == 0 &&
            committed.Receipt.TalentExperienceGained == 0 &&
            committed.Receipt.ProgressionRevision == 1,
            "zero-value eligible death still creates a durable claim");

        var changed = CreateCommand(
            runtimeId,
            experience: 1,
            talentExperience: 1);
        var conflict = await CreateExecutor(source).ExecuteAsync(
            CreateEnvelope(fixture, changed));
        Check.True(
            conflict.Disposition ==
                MonsterDeathRewardExecutionDisposition
                    .RequestHashConflict,
            "a zero reward death cannot later be reused for value");

        var state = await ReadCharacterStateAsync(
            connectionString,
            fixture.CharacterId);
        Check.True(
            state.Experience == 0 &&
            state.TalentExperience == 0 &&
            state.Revision == 1,
            "zero claim and conflict do not mutate progression value");
        await AssertCharacterPurgeLeavesEvidenceAsync(
            connectionString,
            fixture.CharacterId,
            zero.DeathEventId);
    }

    private static PostgresMonsterDeathRewardCommandExecutor
        CreateExecutor(NpgsqlDataSource source) =>
        new(source, new PostgresOutboxDispatcherOptions());

    private static MonsterDeathRewardCommand CreateCommand(
        Guid runtimeId,
        int experience,
        int talentExperience)
    {
        if (!MonsterDeathRewardCommandEnvelope.TryCreateCommand(
                runtimeId,
                mapId: 1,
                monsterObjectId: 77_001,
                spawnGeneration: 3,
                deathHealthRevision: 9,
                experience,
                talentExperience,
                out var command))
        {
            throw new InvalidOperationException(
                "The reward fixture command is invalid.");
        }
        return command;
    }

    private static CommandEnvelope<MonsterDeathRewardCommand>
        CreateEnvelope(
            RewardFixture fixture,
            MonsterDeathRewardCommand command) =>
        PlayerOwnershipTestFences.Bind(
            MonsterDeathRewardCommandEnvelope.Create(
            new CommandSubject(
                fixture.AccountId,
                fixture.CharacterId),
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.LegacyTcp),
            DateTimeOffset.UtcNow,
            command));

    private static async Task<RewardFixture> CreateFixtureAsync(
        string connectionString,
        string scenario)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        int accountId;
        await using (var account = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """,
            connection,
            transaction))
        {
            account.Parameters.AddWithValue(
                "username",
                $"b12_{scenario}_{token}");
            accountId = Convert.ToInt32(
                await account.ExecuteScalarAsync());
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id,
                name,
                camp,
                profession,
                fighter_job_lv,
                fighter_job_exp,
                "SkillExp",
                "SkillPoint"
            )
            VALUES (
                @accountId,
                @name,
                1,
                0,
                80,
                0,
                0,
                0
            )
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue(
                "name",
                $"B12{scenario}{token}");
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync());
        }
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();
        return new RewardFixture(accountId, characterId);
    }

    private static async Task<RaceState> ReadRaceStateAsync(
        string connectionString,
        RewardFixture fixtureA,
        RewardFixture fixtureB,
        Guid deathEventId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                SUM(fighter_job_exp)::integer,
                SUM("SkillExp")::integer,
                SUM(progression_reward_revision)::bigint,
                (
                    SELECT COUNT(*)::integer
                    FROM public.monster_death_reward_settlements
                    WHERE death_event_id = @deathEventId
                ),
                (
                    SELECT COUNT(*)::integer
                    FROM public.command_inbox
                    WHERE command_family =
                        'monster_reward_settlement'
                      AND result_payload ->> 'deathEventId' =
                          @deathEventIdText
                ),
                (
                    SELECT COUNT(*)::integer
                    FROM public.command_audit
                    WHERE command_family =
                        'monster_reward_settlement'
                      AND detail_payload ->> 'deathEventId' =
                          @deathEventIdText
                ),
                (
                    SELECT COUNT(*)::integer
                    FROM public.outbox_events
                    WHERE event_type =
                        'progression.monster_reward_settled'
                      AND payload ->> 'deathEventId' =
                          @deathEventIdText
                )
            FROM public.character_base
            WHERE id IN (@characterA, @characterB);
            """,
            connection);
        command.Parameters.AddWithValue(
            "deathEventId",
            deathEventId);
        command.Parameters.AddWithValue(
            "deathEventIdText",
            deathEventId.ToString());
        command.Parameters.AddWithValue(
            "characterA",
            fixtureA.CharacterId);
        command.Parameters.AddWithValue(
            "characterB",
            fixtureB.CharacterId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The reward race state is missing.");
        }
        return new RaceState(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6));
    }

    private static async Task<CharacterState> ReadCharacterStateAsync(
        string connectionString,
        int characterId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                fighter_job_exp,
                "SkillExp",
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
                "The reward fixture character is missing.");
        }
        return new CharacterState(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt64(2));
    }

    private static async Task AssertCharacterPurgeLeavesEvidenceAsync(
        string connectionString,
        int characterId,
        Guid deathEventId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var delete = new NpgsqlCommand(
            """
            DELETE FROM public.character_base
            WHERE id = @characterId;
            """,
            connection))
        {
            delete.Parameters.AddWithValue(
                "characterId",
                characterId);
            Check.Equal(
                1,
                await delete.ExecuteNonQueryAsync(),
                "character purge is not blocked by reward evidence");
        }
        await using var count = new NpgsqlCommand(
            """
            SELECT COUNT(*)::integer
            FROM public.monster_death_reward_settlements
            WHERE death_event_id = @deathEventId;
            """,
            connection);
        count.Parameters.AddWithValue(
            "deathEventId",
            deathEventId);
        Check.Equal(
            1,
            Convert.ToInt32(await count.ExecuteScalarAsync()),
            "permanent reward evidence survives character purge");
    }

    private readonly record struct RewardFixture(
        int AccountId,
        int CharacterId);

    private readonly record struct RaceState(
        int TotalExperience,
        int TotalTalentExperience,
        long TotalRevision,
        int Settlements,
        int Inboxes,
        int Audits,
        int OutboxEvents);

    private readonly record struct CharacterState(
        int Experience,
        int TalentExperience,
        long Revision);
}
