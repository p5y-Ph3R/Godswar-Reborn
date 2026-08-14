using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetEggHatchPersistenceChecks
{
    private static async Task AssertHatchRankEvidenceAsync(
        string connectionString,
        int characterId,
        params PetEggHatchResult[] expectedResults)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT pet.id,
                   pet.rank,
                   pet.birth_rank,
                   pet.hatch_rank_roll,
                   pet.hatch_rank_outcome_order,
                   pet.hatch_rank_content_revision,
                   (audit.after_state ->> 'birth_rank')::numeric,
                   (audit.after_state ->> 'hatch_rank_roll')::smallint,
                   (audit.after_state ->> 'hatch_rank_outcome_order')::smallint,
                   audit.after_state ->> 'hatch_rank_content_revision'
            FROM public.character_pets pet
            JOIN public.pet_operation_audit audit
              ON audit.pet_id_snapshot = pet.id
             AND audit.operation = 'hatch'
             AND audit.outcome = 'committed'
            WHERE pet.user_id = @characterId
            ORDER BY pet.id;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);

        var rows = new Dictionary<long, HatchRankEvidence>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(
                reader.GetInt64(0),
                new HatchRankEvidence(
                    reader.GetDecimal(1),
                    reader.GetDecimal(2),
                    reader.GetInt16(3),
                    reader.GetInt16(4),
                    reader.GetString(5),
                    reader.GetDecimal(6),
                    reader.GetInt16(7),
                    reader.GetInt16(8),
                    reader.GetString(9)));
        }

        Check.Equal(
            expectedResults.Length,
            rows.Count,
            "every committed hatch retains rank evidence");
        foreach (var expected in expectedResults)
        {
            var roll = expected.HatchRank
                ?? throw new InvalidOperationException(
                    $"Hatch result {expected.PetId} has no rank roll.");
            Check.True(
                rows.TryGetValue(expected.PetId, out var evidence),
                $"hatched pet {expected.PetId} has persistent rank evidence");
            Check.True(
                evidence!.CurrentRank == roll.Rank &&
                evidence.BirthRank == roll.Rank &&
                evidence.Roll == roll.Roll &&
                evidence.OutcomeOrder == roll.OutcomeOrder &&
                evidence.ContentRevision ==
                    expected.HatchRankContentRevision,
                $"hatched pet {expected.PetId} stores its deterministic rank receipt");
            Check.True(
                evidence.AuditRank == roll.Rank &&
                evidence.AuditRoll == roll.Roll &&
                evidence.AuditOutcomeOrder == roll.OutcomeOrder &&
                evidence.AuditContentRevision ==
                    expected.HatchRankContentRevision,
                $"hatch audit {expected.PetId} repeats its source rank evidence");
        }
    }

    private sealed record HatchRankEvidence(
        decimal CurrentRank,
        decimal BirthRank,
        short Roll,
        short OutcomeOrder,
        string ContentRevision,
        decimal AuditRank,
        short AuditRoll,
        short AuditOutcomeOrder,
        string AuditContentRevision);
}
