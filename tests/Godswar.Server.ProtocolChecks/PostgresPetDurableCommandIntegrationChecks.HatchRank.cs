using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertDurableHatchEvidenceAsync(
        NpgsqlDataSource dataSource,
        PetDurableReceipt receipt)
    {
        Check.Equal(
            PetDurablePersistenceCodec.BagItemActivationContractVersion,
            PetDurablePersistenceCodec.ContractVersionFor(
                receipt.Family),
            "bag activation uses the rank-aware durable contract");
        if (!long.TryParse(receipt.AuditReference, out var auditId))
        {
            throw new InvalidDataException(
                "Durable hatch receipt has an invalid audit reference.");
        }

        await using var command = dataSource.CreateCommand(
            """
            SELECT
                (audit.detail_payload #>> '{hatch_rank,rank}')::numeric,
                (audit.detail_payload #>> '{hatch_rank,outcome_order}')::smallint,
                (audit.detail_payload #>> '{hatch_rank,roll}')::smallint,
                audit.detail_payload #>> '{hatch_rank,content_revision}',
                (inbox.result_payload #>> '{HatchRank,Rank}')::numeric,
                inbox.result_payload #>> '{HatchRank,ContentRevision}',
                (outbox.payload #>> '{HatchRank,Rank}')::numeric,
                outbox.payload #>> '{HatchRank,ContentRevision}',
                inbox.result_contract_version,
                outbox.contract_version
            FROM public.command_audit audit
            JOIN public.command_inbox inbox
              ON inbox.audit_id = audit.id
            JOIN public.outbox_events outbox
              ON outbox.command_inbox_id = inbox.id
             AND outbox.consumer_key = 'pet_durable_v1'
            WHERE audit.id = @auditId;
            """);
        command.Parameters.AddWithValue("auditId", auditId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Durable hatch audit/inbox/outbox evidence disappeared.");
        }

        var expected = receipt.HatchRank ??
            throw new InvalidDataException(
                "Durable hatch receipt has no rank evidence.");
        Check.True(
            reader.GetDecimal(0) == expected.Rank &&
            reader.GetInt16(1) == expected.OutcomeOrder &&
            reader.GetInt16(2) == expected.Roll &&
            reader.GetString(3) == expected.ContentRevision &&
            reader.GetDecimal(4) == expected.Rank &&
            reader.GetString(5) == expected.ContentRevision &&
            reader.GetDecimal(6) == expected.Rank &&
            reader.GetString(7) == expected.ContentRevision &&
            reader.GetInt16(8) ==
                PetDurablePersistenceCodec.BagItemActivationContractVersion &&
            reader.GetInt16(9) ==
                PetDurablePersistenceCodec.BagItemActivationContractVersion,
            "durable hatch rank evidence survives pet row, audit, inbox, and outbox");
    }

    private static async Task
        AssertDurableHatchEvidenceSurvivesPetDeletionAsync(
            NpgsqlDataSource dataSource,
            PetDurableReceipt receipt)
    {
        if (!long.TryParse(receipt.AuditReference, out var auditId))
        {
            throw new InvalidDataException(
                "Durable hatch receipt has an invalid audit reference.");
        }

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var delete = new NpgsqlCommand(
            "DELETE FROM public.character_pets WHERE id = @petId;",
            connection,
            transaction))
        {
            delete.Parameters.AddWithValue("petId", receipt.PetId);
            Check.Equal(
                1,
                await delete.ExecuteNonQueryAsync(),
                "durable hatch fixture pet can be consumed independently");
        }

        await using (var evidence = new NpgsqlCommand(
            """
            SELECT
                count(*) FILTER (
                    WHERE audit.detail_payload #> '{hatch_rank}'
                        IS NOT NULL),
                count(*) FILTER (
                    WHERE inbox.result_payload #> '{HatchRank}'
                        IS NOT NULL),
                count(*) FILTER (
                    WHERE outbox.payload #> '{HatchRank}'
                        IS NOT NULL)
            FROM public.command_audit audit
            JOIN public.command_inbox inbox
              ON inbox.audit_id = audit.id
            JOIN public.outbox_events outbox
              ON outbox.command_inbox_id = inbox.id
             AND outbox.consumer_key = 'pet_durable_v1'
            WHERE audit.id = @auditId;
            """,
            connection,
            transaction))
        {
            evidence.Parameters.AddWithValue("auditId", auditId);
            await using var reader = await evidence.ExecuteReaderAsync();
            Check.True(
                await reader.ReadAsync() &&
                reader.GetInt64(0) == 1 &&
                reader.GetInt64(1) == 1 &&
                reader.GetInt64(2) == 1,
                "rank provenance remains durable after a deputy pet row is consumed");
        }

        await transaction.RollbackAsync();
    }

    private sealed class FixedPetHatchRankRollSource(int roll) :
        IPetHatchRankRollSource
    {
        public int NextRoll() => roll;
    }

    private sealed class ThrowingPetHatchRankRollSource :
        IPetHatchRankRollSource
    {
        public int NextRoll() => throw new InvalidOperationException(
            "A duplicate durable hatch must never reroll rank.");
    }
}
