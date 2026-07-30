using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Zodiac;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresZodiacSkillGridActivationCommandIntegrationChecks
{
    private static async Task<ZodiacActivationFixture>
        CreateFixtureAsync(
            string connectionString,
            string scenario,
            int gold)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var shortScenario = scenario[..Math.Min(6, scenario.Length)];
        var username = $"b09zac_{shortScenario}_{token}";
        var characterName = $"ZA{shortScenario}{token}";

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
                name,
                camp,
                profession,
                fighter_job_lv,
                "Money",
                "Stone",
                wallet_revision,
                inventory_revision
            )
            VALUES (
                @accountId,
                @name,
                1,
                0,
                80,
                1000,
                @gold,
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
            character.Parameters.AddWithValue("gold", gold);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The Zodiac fixture character has no identity."));
        }

        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                30,
                CancellationToken.None),
            "Zodiac fixture captures an economy baseline");
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();
        return new ZodiacActivationFixture(
            accountId,
            characterId,
            username,
            new CommandSubject(accountId, characterId));
    }

    private static async Task<ZodiacDurableState> ReadStateAsync(
        string connectionString,
        ZodiacActivationFixture fixture,
        int gridIndex)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                cb."Stone",
                cb.wallet_revision,
                COALESCE(grid.level, 0)::integer,
                COALESCE(grid.selected_skill_id, -1)::integer,
                COALESCE((
                    SELECT count(*)
                    FROM public.command_audit audit
                    WHERE audit.principal_type = @principalType
                      AND audit.principal_key = @principalKey
                      AND audit.aggregate_type = @aggregateType
                      AND audit.aggregate_key = @aggregateKey
                      AND audit.command_family = @commandFamily
                ), 0),
                COALESCE((
                    SELECT count(*)
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @commandFamily
                ), 0),
                COALESCE((
                    SELECT max(inbox.duplicate_count)
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @commandFamily
                ), 0)::integer,
                COALESCE((
                    SELECT max(inbox.request_conflict_count)
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @commandFamily
                ), 0)::integer,
                COALESCE((
                    SELECT count(*)
                    FROM public.character_currency_ledger ledger
                    WHERE ledger.account_id = @accountId
                      AND ledger.character_id = @characterId
                      AND ledger.currency_code = 'gold'
                      AND ledger.reason_code = @reasonCode
                ), 0),
                COALESCE((
                    SELECT sum(ledger.delta)
                    FROM public.character_currency_ledger ledger
                    WHERE ledger.account_id = @accountId
                      AND ledger.character_id = @characterId
                      AND ledger.currency_code = 'gold'
                      AND ledger.reason_code = @reasonCode
                ), 0)::bigint,
                COALESCE((
                    SELECT count(*)
                    FROM public.outbox_events outbox
                    WHERE outbox.aggregate_type = @aggregateType
                      AND outbox.aggregate_key = @aggregateKey
                      AND outbox.event_type = @eventType
                ), 0),
                COALESCE((
                    SELECT bool_and(
                        outbox.consumer_key = @consumerKey
                        AND outbox.ordering_policy = @orderingPolicy
                        AND outbox.contract_version = @contractVersion
                        AND outbox.aggregate_version =
                            @aggregateRevision)
                    FROM public.outbox_events outbox
                    WHERE outbox.aggregate_type = @aggregateType
                      AND outbox.aggregate_key = @aggregateKey
                      AND outbox.event_type = @eventType
                ), false),
                COALESCE((
                    SELECT is_reconciled
                    FROM public.character_wallet_reconciliation
                    WHERE character_id = @characterId
                ), false)
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

        return new ZodiacDurableState(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetBoolean(11),
            reader.GetBoolean(12));
    }

    private static void AddStateParameters(
        NpgsqlCommand command,
        ZodiacActivationFixture fixture,
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
            ZodiacSkillGridActivationPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateType",
            ZodiacSkillGridActivationPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            ZodiacSkillGridActivationPersistenceCodec.AggregateKey(
                fixture.CharacterId,
                gridIndex));
        command.Parameters.AddWithValue(
            "commandFamily",
            ZodiacSkillGridActivationPersistenceCodec.CommandFamily);
        command.Parameters.AddWithValue(
            "reasonCode",
            ZodiacSkillGridActivationPersistenceCodec.LedgerReasonCode);
        command.Parameters.AddWithValue(
            "eventType",
            ZodiacSkillGridActivationPersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "consumerKey",
            ZodiacSkillGridActivationPersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            ZodiacSkillGridActivationPersistenceCodec.OrderingPolicy);
        command.Parameters.AddWithValue(
            "contractVersion",
            ZodiacSkillGridActivationPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "aggregateRevision",
            ZodiacSkillGridActivationPersistenceCodec.AggregateRevision);
    }

    private static async Task AddDurableGoldAsync(
        string connectionString,
        ZodiacActivationFixture fixture,
        int goldAfter)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        int goldBefore;
        long revisionBefore;
        await using (var read = new NpgsqlCommand(
            """
            SELECT "Stone", wallet_revision
            FROM public.character_base
            WHERE id = @characterId
              AND account_id = @accountId
            FOR UPDATE;
            """,
            connection,
            transaction))
        {
            read.Parameters.AddWithValue(
                "characterId",
                fixture.CharacterId);
            read.Parameters.AddWithValue("accountId", fixture.AccountId);
            await using var reader = await read.ExecuteReaderAsync();
            Check.True(
                await reader.ReadAsync(),
                "top-up fixture locks its character");
            goldBefore = reader.GetInt32(0);
            revisionBefore = reader.GetInt64(1);
        }

        Check.True(
            goldAfter > goldBefore,
            "fixture top-up must increase Gold");
        var operationId = RandomNumberGenerator.GetBytes(32);
        var requestHash = RandomNumberGenerator.GetBytes(32);
        var resultPayload = "{}";
        var resultHash =
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                resultPayload));
        const string family = "test_wallet_topup";
        var principalKey =
            fixture.AccountId.ToString(CultureInfo.InvariantCulture);
        var aggregateKey = $"character:{fixture.CharacterId}:wallet";

        long auditId;
        await using (var audit = new NpgsqlCommand(
            """
            INSERT INTO public.command_audit (
                principal_type, principal_key,
                aggregate_type, aggregate_key, command_family,
                operation_id, request_hash, outcome_code,
                detail_payload, retention_policy
            )
            VALUES (
                'account', @principalKey,
                'test_wallet', @aggregateKey, @commandFamily,
                @operationId, @requestHash, 'committed',
                '{}'::jsonb, 'permanent'
            )
            RETURNING id;
            """,
            connection,
            transaction))
        {
            AddTopUpIdentity(
                audit,
                principalKey,
                aggregateKey,
                family,
                operationId,
                requestHash);
            auditId = Convert.ToInt64(
                await audit.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The top-up audit has no identity."));
        }

        long inboxId;
        await using (var inbox = new NpgsqlCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type, principal_key,
                aggregate_type, aggregate_key, command_family,
                operation_id, request_hash,
                result_contract_version, result_code,
                result_payload, result_hash, audit_id,
                retention_policy
            )
            VALUES (
                'account', @principalKey,
                'test_wallet', @aggregateKey, @commandFamily,
                @operationId, @requestHash,
                1, 'committed',
                @resultPayload, @resultHash, @auditId,
                'permanent'
            )
            RETURNING id;
            """,
            connection,
            transaction))
        {
            AddTopUpIdentity(
                inbox,
                principalKey,
                aggregateKey,
                family,
                operationId,
                requestHash);
            inbox.Parameters.Add(
                "resultPayload",
                NpgsqlDbType.Jsonb).Value = resultPayload;
            inbox.Parameters.Add(
                "resultHash",
                NpgsqlDbType.Bytea).Value = resultHash;
            inbox.Parameters.AddWithValue("auditId", auditId);
            inboxId = Convert.ToInt64(
                await inbox.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The top-up inbox has no identity."));
        }

        var revisionAfter = checked(revisionBefore + 1);
        await using (var update = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET "Stone" = @goldAfter,
                wallet_revision = @revisionAfter
            WHERE id = @characterId
              AND account_id = @accountId
              AND "Stone" = @goldBefore
              AND wallet_revision = @revisionBefore;
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("goldAfter", goldAfter);
            update.Parameters.AddWithValue(
                "revisionAfter",
                revisionAfter);
            update.Parameters.AddWithValue(
                "characterId",
                fixture.CharacterId);
            update.Parameters.AddWithValue("accountId", fixture.AccountId);
            update.Parameters.AddWithValue("goldBefore", goldBefore);
            update.Parameters.AddWithValue(
                "revisionBefore",
                revisionBefore);
            Check.Equal(
                1,
                await update.ExecuteNonQueryAsync(),
                "top-up fixture advances the wallet once");
        }

        await using (var ledger = new NpgsqlCommand(
            """
            INSERT INTO public.character_currency_ledger (
                command_inbox_id, account_id, character_id,
                wallet_revision, currency_code, delta,
                balance_before, balance_after, reason_code
            )
            VALUES (
                @inboxId, @accountId, @characterId,
                @revisionAfter, 'gold', @delta,
                @goldBefore, @goldAfter, 'test_wallet_topup'
            );
            """,
            connection,
            transaction))
        {
            ledger.Parameters.AddWithValue("inboxId", inboxId);
            ledger.Parameters.AddWithValue("accountId", fixture.AccountId);
            ledger.Parameters.AddWithValue(
                "characterId",
                fixture.CharacterId);
            ledger.Parameters.AddWithValue(
                "revisionAfter",
                revisionAfter);
            ledger.Parameters.AddWithValue(
                "delta",
                checked(goldAfter - goldBefore));
            ledger.Parameters.AddWithValue("goldBefore", goldBefore);
            ledger.Parameters.AddWithValue("goldAfter", goldAfter);
            Check.Equal(
                1,
                await ledger.ExecuteNonQueryAsync(),
                "top-up fixture records one currency ledger row");
        }

        await transaction.CommitAsync();
    }

    private static void AddTopUpIdentity(
        NpgsqlCommand command,
        string principalKey,
        string aggregateKey,
        string commandFamily,
        byte[] operationId,
        byte[] requestHash)
    {
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue("commandFamily", commandFamily);
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
    }

    private sealed record ZodiacActivationFixture(
        int AccountId,
        int CharacterId,
        string Username,
        CommandSubject Subject);

    private sealed record ZodiacDurableState(
        int Gold,
        long WalletRevision,
        int Level,
        int SelectedSkillId,
        long AuditCount,
        long InboxCount,
        int DuplicateCount,
        int ConflictCount,
        long CurrencyLedgerCount,
        long GoldLedgerDelta,
        long OutboxCount,
        bool HasStrictOutbox,
        bool WalletReconciled);
}
