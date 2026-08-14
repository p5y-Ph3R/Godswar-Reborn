using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertZeroSpiritPetMergeAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        GameplayItemContent itemContent)
    {
        var fixture = await CreatePetMergeFixtureAsync(
            connectionString,
            itemContent);
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        await SeedSecondPetEggAsync(dataSource, fixture.CharacterId);
        var first = await executor.ExecuteAsync(
            CreatePetMergeHatchEnvelope(
                subject,
                correlation,
                Guid.NewGuid(),
                fixture.EggSlot));
        // These are two independent fixture setup actions. Expire the stock
        // one-second egg cooldown so the second hatch can establish the
        // deputy without weakening production cooldown enforcement.
        await ExpireConsumableCooldownAsync(
            dataSource,
            fixture.CharacterId,
            cooldownGroup: 4740);
        var second = await executor.ExecuteAsync(
            CreatePetMergeHatchEnvelope(
                subject,
                correlation,
                Guid.NewGuid(),
                fixture.EggSlot - 1));
        Check.True(
            first.IsSuccess && second.IsSuccess,
            "zero-spirit Merge fixture hatches both pets");
        var primaryPetId = second.Receipt!.PetId;
        var deputyPetId = first.Receipt!.PetId;
        await SeedPetMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            primaryPetId,
            deputyPetId);
        await DeletePetMergeMaterialsAsync(
            dataSource,
            fixture.CharacterId);
        var before = await ReadPetMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            primaryPetId,
            deputyPetId);

        var envelope = CreateZeroSpiritPetMergeEnvelope(
            subject,
            correlation,
            Guid.NewGuid(),
            primaryPetId,
            deputyPetId);
        var concurrent = await Task.WhenAll(
            executor.ExecuteAsync(envelope),
            restarted.ExecuteAsync(envelope));
        AssertCommitAndDuplicate(
            concurrent,
            PetDurableReceiptStatus.PetToPetMerged,
            "concurrent zero-spirit pet-to-pet Merge");
        var receipt = concurrent.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var after = await ReadPetMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            primaryPetId,
            deputyPetId);
        var delta = receipt.PetMergeDelta ??
            throw new InvalidDataException(
                "Zero-spirit Merge receipt lost its exact gains.");
        Check.True(
            after.PrimaryExists &&
            !after.DeputyExists &&
            after.InventoryRevision == before.InventoryRevision &&
            after.InventoryLedgerCount == before.InventoryLedgerCount &&
            after.InventoryOutboxCount == before.InventoryOutboxCount &&
            after.PetMergeAuditCount == before.PetMergeAuditCount + 1 &&
            after.PetMergeCommittedAuditCount ==
                before.PetMergeCommittedAuditCount + 1 &&
            after.CommandInboxCount == before.CommandInboxCount + 1 &&
            after.CommandAuditCount == before.CommandAuditCount + 1 &&
            after.CommandOutboxCount == before.CommandOutboxCount + 1 &&
            after.EvidenceViewCount == before.EvidenceViewCount + 1 &&
            after.ConsumedAuditQuantity == 0 &&
            receipt.KitBagSlot == -1 &&
            delta.IsValid,
            "zero-spirit Merge commits no inventory mutation and retains exact deltas");
        await AssertZeroSpiritPetMergeAuditAsync(
            dataSource,
            fixture.CharacterId);

        var replay = await restarted.ExecuteAsync(envelope);
        var afterReplay = await ReadPetMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            primaryPetId,
            deputyPetId);
        Check.True(
            replay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replay.Receipt == receipt &&
            PetMergeStateEquals(afterReplay, after),
            "zero-spirit Merge retry reuses its durable random result");
    }

    private static async Task AssertZeroSpiritPetMergeAuditAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                (before_state ->> 'material_item_id')::integer,
                (before_state ->> 'material_quantity')::integer,
                (before_state #>> '{savvy_evidence,spirit_count}')::integer,
                (before_state #>> '{rank_evidence,spirit_count}')::integer,
                jsonb_array_length(before_state #> '{savvy_evidence,stats}'),
                jsonb_array_length(consumed_items),
                after_state ->> 'deputy_pet_id'
            FROM public.pet_operation_audit
            WHERE user_id_snapshot = @characterId
              AND operation = 'pet_merge'
              AND outcome = 'committed';
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt32(0) == 0 &&
            reader.GetInt32(1) == 0 &&
            reader.GetInt32(2) == 0 &&
            reader.GetInt32(3) == 0 &&
            reader.GetInt32(4) == 6 &&
            reader.GetInt32(5) == 0 &&
            reader.IsDBNull(6) &&
            !await reader.ReadAsync(),
            "zero-spirit audit pins both policies, six Savvy rows, no consumed item, and deputy deletion");
    }

    private static CommandEnvelope<PetToPetMergeCommand>
        CreateZeroSpiritPetMergeEnvelope(
            CommandSubject subject,
            CommandConnectionCorrelation correlation,
            Guid operationId,
            long primaryPetId,
            long deputyPetId) =>
        PlayerOwnershipTestFences.Bind(
            PetToPetMergeCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new PetToPetMergeCommand(
                    PetCommandOperationIdentity.RawLocalServer(
                        operationId,
                        correlation.ConnectionId),
                    primaryPetId,
                    deputyPetId,
                    MaterialItemId: 0,
                    MaterialQuantity: 0)));

    private static async Task DeletePetMergeMaterialsAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            DELETE FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND prop_id = 10103;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        Check.Equal(
            5,
            await command.ExecuteNonQueryAsync(),
            "zero-spirit fixture removes its unused Merge Spirits");
    }
}
