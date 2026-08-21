using System.Globalization;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Zodiac;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresZodiacSkillGridUpgradeCommandIntegrationChecks
{
    private static async Task<ZodiacUpgradeFixture> CreateFixtureAsync(
        string connectionString,
        string scenario,
        int energy,
        int energyRemainderX100,
        int talentPoints,
        byte zodiacLevel = 30,
        byte gridLevel = 1,
        int secondGridIndex = -1)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var shortScenario = scenario[..Math.Min(6, scenario.Length)];
        var username = $"b09zup_{shortScenario}_{token}";
        var characterName = $"ZU{shortScenario}{token}";
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
            account.Parameters.AddWithValue("username", username);
            accountId = Convert.ToInt32(
                await account.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The Zodiac fixture account has no identity."));
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id,
                server_id,
                name,
                camp,
                profession,
                fighter_job_lv,
                zodiac_level,
                zodiac_energy,
                zodiac_energy_remainder_x100,
                "SkillPoint",
                "Money",
                "Stone",
                wallet_revision,
                inventory_revision
            )
            VALUES (
                @accountId,
                1,
                @name,
                1,
                0,
                80,
                @zodiacLevel,
                @energy,
                @energyRemainderX100,
                @talentPoints,
                1000,
                1000,
                0,
                0
            )
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue("name", characterName);
            character.Parameters.AddWithValue(
                "zodiacLevel",
                checked((short)zodiacLevel));
            character.Parameters.AddWithValue("energy", energy);
            character.Parameters.AddWithValue(
                "energyRemainderX100",
                energyRemainderX100);
            character.Parameters.AddWithValue(
                "talentPoints",
                talentPoints);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The Zodiac fixture character has no identity."));
        }

        await InsertGridAsync(
            connection,
            transaction,
            characterId,
            gridIndex: 0,
            gridLevel);
        if (secondGridIndex >= 0)
        {
            await InsertGridAsync(
                connection,
                transaction,
                characterId,
                secondGridIndex,
                gridLevel);
        }

        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();
        return new ZodiacUpgradeFixture(
            accountId,
            characterId,
            new CommandSubject(accountId, characterId));
    }

    private static async Task InsertGridAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int gridIndex,
        byte level)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_zodiac_skill_grids (
                user_id,
                grid_index,
                level,
                selected_skill_id
            )
            VALUES (@characterId, @gridIndex, @level, -1);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "gridIndex",
            checked((short)gridIndex));
        command.Parameters.AddWithValue("level", checked((short)level));
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "Zodiac fixture inserts one active grid");
    }

    private static async Task SetResourcesAsync(
        string connectionString,
        ZodiacUpgradeFixture fixture,
        int energy,
        int remainder,
        int talentPoints)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET zodiac_energy = @energy,
                zodiac_energy_remainder_x100 = @remainder,
                "SkillPoint" = @talentPoints
            WHERE account_id = @accountId
              AND id = @characterId;
            """,
            connection);
        command.Parameters.AddWithValue("energy", energy);
        command.Parameters.AddWithValue("remainder", remainder);
        command.Parameters.AddWithValue(
            "talentPoints",
            talentPoints);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "Zodiac fixture resource update is exact");
    }

    private static async Task<ZodiacUpgradeDurableState> ReadStateAsync(
        string connectionString,
        ZodiacUpgradeFixture fixture,
        int gridIndex)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                cb.zodiac_energy,
                cb.zodiac_energy_remainder_x100,
                cb."SkillPoint",
                COALESCE(grid.level, 0)::integer,
                COALESCE(grid.selected_skill_id, -1)::integer,
                COALESCE((
                    SELECT count(*)
                    FROM public.command_audit audit
                    WHERE audit.principal_type = @principalType
                      AND audit.principal_key = @principalKey
                      AND audit.aggregate_type = @aggregateType
                      AND audit.aggregate_key = @commandAggregateKey
                      AND audit.command_family = @commandFamily
                ), 0),
                COALESCE((
                    SELECT count(*)
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @commandAggregateKey
                      AND inbox.command_family = @commandFamily
                ), 0),
                COALESCE((
                    SELECT max(inbox.duplicate_count)
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @commandAggregateKey
                      AND inbox.command_family = @commandFamily
                ), 0)::integer,
                COALESCE((
                    SELECT max(inbox.request_conflict_count)
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @commandAggregateKey
                      AND inbox.command_family = @commandFamily
                ), 0)::integer,
                COALESCE((
                    SELECT count(*)
                    FROM public.outbox_events outbox
                    WHERE outbox.aggregate_type = @aggregateType
                      AND outbox.aggregate_key = @eventAggregateKey
                      AND outbox.event_type = @eventType
                ), 0),
                COALESCE((
                    SELECT
                        bool_and(
                            outbox.consumer_key = @consumerKey
                            AND outbox.ordering_policy = @orderingPolicy
                            AND outbox.contract_version =
                                @contractVersion
                            AND outbox.payload -> 'currentLevel' =
                                to_jsonb(outbox.aggregate_version)
                            AND outbox.payload ->> 'energyBefore'
                                IS NOT NULL
                            AND outbox.payload ->> 'energyAfter'
                                IS NOT NULL
                            AND outbox.payload ->> 'talentPointsBefore'
                                IS NOT NULL
                            AND outbox.payload ->> 'talentPointsAfter'
                                IS NOT NULL
                        )
                        AND max(outbox.aggregate_version) = grid.level
                    FROM public.outbox_events outbox
                    WHERE outbox.aggregate_type = @aggregateType
                      AND outbox.aggregate_key = @eventAggregateKey
                      AND outbox.event_type = @eventType
                ), false),
                COALESCE((
                    SELECT bool_and(
                        audit.detail_payload ->> 'energyBefore'
                            IS NOT NULL
                        AND audit.detail_payload ->> 'energyAfter'
                            IS NOT NULL
                        AND audit.detail_payload ->> 'talentPointsBefore'
                            IS NOT NULL
                        AND audit.detail_payload ->> 'talentPointsAfter'
                            IS NOT NULL
                    )
                    FROM public.command_audit audit
                    WHERE audit.principal_type = @principalType
                      AND audit.principal_key = @principalKey
                      AND audit.aggregate_type = @aggregateType
                      AND audit.aggregate_key = @commandAggregateKey
                      AND audit.command_family = @commandFamily
                ), false),
                COALESCE((
                    SELECT count(*)
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @commandAggregateKey
                      AND inbox.command_family = @commandFamily
                      AND inbox.result_code = 'terminal_rejected'
                ), 0),
                COALESCE((
                    SELECT count(*)
                    FROM public.character_currency_ledger ledger
                    WHERE ledger.account_id = @accountId
                      AND ledger.character_id = @characterId
                ), 0)
            FROM public.character_base cb
            LEFT JOIN public.character_zodiac_skill_grids grid
              ON grid.user_id = cb.id
             AND grid.grid_index = @gridIndex
            WHERE cb.id = @characterId
              AND cb.account_id = @accountId;
            """,
            connection);
        AddStateParameters(command, fixture, gridIndex);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The Zodiac fixture character disappeared.");
        }

        return new ZodiacUpgradeDurableState(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt64(9),
            reader.GetBoolean(10),
            reader.GetBoolean(11),
            reader.GetInt64(12),
            reader.GetInt64(13));
    }

    private static void AddStateParameters(
        NpgsqlCommand command,
        ZodiacUpgradeFixture fixture,
        int gridIndex)
    {
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "gridIndex",
            checked((short)gridIndex));
        command.Parameters.AddWithValue(
            "principalType",
            ZodiacSkillGridUpgradePersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateType",
            ZodiacSkillGridUpgradePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "commandAggregateKey",
            ZodiacSkillGridUpgradePersistenceCodec.CommandAggregateKey(
                fixture.CharacterId));
        command.Parameters.AddWithValue(
            "eventAggregateKey",
            ZodiacSkillGridUpgradePersistenceCodec.EventAggregateKey(
                fixture.CharacterId,
                gridIndex));
        command.Parameters.AddWithValue(
            "commandFamily",
            ZodiacSkillGridUpgradePersistenceCodec.CommandFamily);
        command.Parameters.AddWithValue(
            "eventType",
            ZodiacSkillGridUpgradePersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "consumerKey",
            ZodiacSkillGridUpgradePersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            ZodiacSkillGridUpgradePersistenceCodec.OrderingPolicy);
        command.Parameters.AddWithValue(
            "contractVersion",
            ZodiacSkillGridUpgradePersistenceCodec.ContractVersion);
    }

    private sealed record ZodiacUpgradeFixture(
        int AccountId,
        int CharacterId,
        CommandSubject Subject);

    private sealed record ZodiacUpgradeDurableState(
        int Energy,
        int EnergyRemainderX100,
        int TalentPoints,
        int Level,
        int SelectedSkillId,
        long AuditCount,
        long InboxCount,
        int DuplicateCount,
        int ConflictCount,
        long OutboxCount,
        bool HasLatestWinsEvidence,
        bool HasAuditResourceEvidence,
        long TerminalRejectedCount,
        long CurrencyLedgerCount);
}
