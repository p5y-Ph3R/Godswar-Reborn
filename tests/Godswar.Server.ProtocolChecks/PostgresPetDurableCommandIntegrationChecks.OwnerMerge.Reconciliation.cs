using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertOwnerMergeRevisionReconciliationAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        GameplayItemContent itemContent,
        IPetOwnerMergeContentCatalog ownerMergeContent)
    {
        var fixture = await CreateOwnerMergeRevisionFixtureAsync(
            connectionString,
            itemContent);
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var hatch = await executor.ExecuteAsync(
            CreateOwnerMergeActivationEnvelope(
                subject,
                correlation,
                Guid.NewGuid(),
                fixture.EggSlot));
        Check.True(
            hatch.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            hatch.Receipt?.Status == PetDurableReceiptStatus.EggHatched,
            "owner-Merge reconciliation fixture hatches one pet");
        var petId = hatch.Receipt!.PetId;
        await SeedOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);

        var merge = await executor.ExecuteAsync(
            CreateOwnerMergeToggleEnvelope(
                subject,
                correlation,
                Guid.NewGuid()));
        Check.True(
            merge.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            merge.Receipt?.Status == PetDurableReceiptStatus.OwnerMerged,
            "owner-Merge reconciliation fixture merges once");
        var expected = PetOwnerMergeContributionCalculator.Calculate(
            await ReadOwnerMergeTotalSavvyAsync(dataSource, petId),
            ownerMergeContent);
        var expectedEffects = PetOwnerMergeContributionCalculator
            .ToEffectValues(expected);
        var before = await ReadOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        await CorruptOwnerMergeRowsForReconciliationAsync(
            dataSource,
            petId);

        Check.Equal(
            1,
            await PostgresPetOwnerMergeBonusReconciler.ReconcileAsync(
                dataSource,
                ownerMergeContent),
            "startup reconciliation rebuilds one stale active owner-Merge");
        var rows = await ReadReconciledOwnerMergeRowsAsync(
            dataSource,
            petId);
        Check.Equal(
            16,
            rows.Count,
            "startup reconciliation restores all 16 owner-Merge rows");
        for (var index = 0; index < expectedEffects.Count; index++)
        {
            Check.True(
                rows[index].EffectCode ==
                    (short)expectedEffects[index].Effect &&
                rows[index].EffectValue == expectedEffects[index].Value &&
                rows[index].PetRevision == before.PetRevision &&
                rows[index].BalanceRevision ==
                    ownerMergeContent.Revision.Sha256,
                $"reconciled owner-Merge row {index} is exact and pinned");
        }

        var after = await ReadOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            after.Contributes &&
            after.PetRevision == before.PetRevision &&
            after.BonusCount == 16,
            "reconciliation replaces only the derived owner-Merge rows");
        Check.Equal(
            0,
            await PostgresPetOwnerMergeBonusReconciler.ReconcileAsync(
                dataSource,
                ownerMergeContent),
            "owner-Merge reconciliation is idempotent once rows are current");
    }

    private static async Task
        CorruptOwnerMergeRowsForReconciliationAsync(
            NpgsqlDataSource dataSource,
            long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pet_character_bonuses
            SET effect_value = 999999
            WHERE pet_id = @petId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        Check.Equal(
            16,
            await command.ExecuteNonQueryAsync(),
            "owner-Merge reconciliation fixture corrupts values while retaining current metadata");
    }

    private static async Task<IReadOnlyList<ReconciledOwnerMergeRow>>
        ReadReconciledOwnerMergeRowsAsync(
            NpgsqlDataSource dataSource,
            long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT effect_code, effect_value, revision, balance_revision
            FROM public.character_pet_character_bonuses
            WHERE pet_id = @petId
            ORDER BY effect_code;
            """);
        command.Parameters.AddWithValue("petId", petId);
        var rows = new List<ReconciledOwnerMergeRow>(16);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ReconciledOwnerMergeRow(
                reader.GetInt16(0),
                reader.GetDecimal(1),
                reader.GetInt64(2),
                reader.GetString(3)));
        }
        return rows;
    }

    private readonly record struct ReconciledOwnerMergeRow(
        short EffectCode,
        decimal EffectValue,
        long PetRevision,
        string BalanceRevision);
}
