using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertPetMergeAuditAfterStateAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long expectedDeputyPetId,
        long expectedRevision,
        int expectedCompletedMerges,
        decimal expectedRank,
        PetToPetMergeDelta expectedDelta)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                (after_state ->> 'primary_revision')::bigint,
                (after_state ->> 'primary_completed_merges')::integer,
                (after_state ->> 'primary_rank')::numeric,
                after_state ->> 'deputy_pet_id',
                after_state ->> 'pet_content_revision',
                (before_state ->> 'deputy_pet_id')::bigint,
                (before_state ->> 'deputy_species_id')::smallint,
                (before_state ->> 'deputy_aptitude')::smallint,
                (before_state ->> 'deputy_rank')::numeric,
                (before_state ->> 'material_item_id')::integer,
                (before_state ->> 'material_quantity')::integer,
                before_state #>> '{rank_evidence,policy_revision}',
                before_state #>> '{rank_evidence,content_revision}',
                (before_state #>> '{rank_evidence,primary_rank_hundredths}')::integer,
                (before_state #>> '{rank_evidence,deputy_rank_hundredths}')::integer,
                (before_state #>> '{rank_evidence,rank_difference_hundredths}')::integer,
                (before_state #>> '{rank_evidence,lookup_minimum_rank_difference}')::integer,
                (before_state #>> '{rank_evidence,lookup_base_increase}')::integer,
                (before_state #>> '{rank_evidence,species_factor}')::numeric,
                (before_state #>> '{rank_evidence,spirit_count}')::integer,
                (before_state #>> '{rank_evidence,minimum_percent}')::integer,
                (before_state #>> '{rank_evidence,maximum_percent}')::integer,
                (before_state #>> '{rank_evidence,factor_adjusted_base_increase}')::integer,
                (before_state #>> '{rank_evidence,uncapped_minimum_increase}')::integer,
                (before_state #>> '{rank_evidence,uncapped_maximum_increase}')::integer,
                (before_state #>> '{rank_evidence,remaining_to_cap}')::integer,
                (before_state #>> '{rank_evidence,effective_minimum_increase}')::integer,
                (before_state #>> '{rank_evidence,effective_maximum_increase}')::integer,
                (before_state #>> '{rank_evidence,rolled_increase}')::integer,
                (before_state #>> '{rank_evidence,cap_applied}')::boolean,
                (before_state #>> '{rank_evidence,maximum_rank_hundredths}')::integer,
                before_state #>> '{savvy_evidence,policy_revision}',
                before_state #>> '{savvy_evidence,content_revision}',
                (before_state #>> '{savvy_evidence,deputy_species_id}')::integer,
                (before_state #>> '{savvy_evidence,species_factor}')::numeric,
                (before_state #>> '{savvy_evidence,spirit_count}')::integer,
                (before_state #>> '{savvy_evidence,minimum_percent}')::integer,
                (before_state #>> '{savvy_evidence,maximum_percent}')::integer,
                jsonb_array_length(before_state #> '{savvy_evidence,stats}'),
                (before_state #>> '{savvy_evidence,stats,0,stat_code}')::integer,
                (before_state #>> '{savvy_evidence,stats,0,minimum_increase_hundredths}')::integer,
                (before_state #>> '{savvy_evidence,stats,0,maximum_increase_hundredths}')::integer,
                (before_state #>> '{savvy_evidence,stats,0,rolled_increase_hundredths}')::integer,
                (before_state #>> '{rank_evidence,applied_species_factor}')::numeric
            FROM public.pet_operation_audit
            WHERE user_id_snapshot = @characterId
              AND operation = 'pet_merge'
              AND outcome = 'committed';
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt64(0) == expectedRevision &&
            reader.GetInt32(1) == expectedCompletedMerges &&
            reader.GetDecimal(2) == expectedRank &&
            reader.IsDBNull(3) &&
            reader.GetString(4) ==
                PetContentBaseline.Create().Revision.Sha256 &&
            reader.GetInt64(5) == expectedDeputyPetId &&
            reader.GetInt16(6) == 1 &&
            reader.GetInt16(7) == (short)PetAptitude.Smart &&
            reader.GetDecimal(8) == 10m &&
            reader.GetInt32(9) == 10103 &&
            reader.GetInt32(10) == 5 &&
            reader.GetString(11) == PetMergeRankPolicy.PolicyRevision &&
            reader.GetString(12) ==
                PetContentBaseline.Create().Revision.Sha256 &&
            reader.GetInt32(13) == 1000 &&
            reader.GetInt32(14) == 1000 &&
            reader.GetInt32(15) == 0 &&
            reader.GetInt32(16) == 0 &&
            reader.GetInt32(17) == 250 &&
            reader.GetDecimal(18) == 1.4m &&
            reader.GetInt32(19) == 5 &&
            reader.GetInt32(20) == 50 &&
            reader.GetInt32(21) == 100 &&
            reader.GetInt32(22) == 350 &&
            reader.GetInt32(23) == 175 &&
            reader.GetInt32(24) == 350 &&
            reader.GetInt32(25) == 64535 &&
            reader.GetInt32(26) == 175 &&
            reader.GetInt32(27) == 350 &&
            reader.GetInt32(28) == expectedDelta.Rank &&
            !reader.GetBoolean(29) &&
            reader.GetInt32(30) == 65535 &&
            reader.GetString(31) == PetMergeSavvyPolicy.PolicyRevision &&
            reader.GetString(32) ==
                PetContentBaseline.Create().Revision.Sha256 &&
            reader.GetInt32(33) == 1 &&
            reader.GetDecimal(34) == 1.4m &&
            reader.GetInt32(35) == 5 &&
            reader.GetInt32(36) == 50 &&
            reader.GetInt32(37) == 100 &&
            reader.GetInt32(38) == 6 &&
            reader.GetInt32(39) == 1 &&
            reader.GetInt32(40) >= 0 &&
            reader.GetInt32(41) >= reader.GetInt32(40) &&
            reader.GetInt32(42) == expectedDelta.Agility &&
            reader.GetDecimal(43) == 1.4m &&
            !await reader.ReadAsync(),
            "pet Merge audit preserves primary, deputy, six Savvy bounds/draws, and rank evidence");
    }
}
