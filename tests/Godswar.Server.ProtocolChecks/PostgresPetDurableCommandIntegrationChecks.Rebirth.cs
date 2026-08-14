using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private const short RebirthSpiritFirstSlot = 80;
    private const short RebirthSpiritSecondSlot = 81;
    private const short RestrictedRebirthSpiritSlot = 82;

    private static async Task AssertPetRebirthAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        GameplayItemContent itemContent)
    {
        await AssertPetRebirthExperienceGatesAsync(
            connectionString,
            dataSource,
            executor,
            itemContent);
        await AssertZeroSpiritPetRebirthAsync(
            connectionString,
            dataSource,
            executor,
            itemContent);
        await AssertPetRebirthCarryOverflowAsync(
            connectionString,
            dataSource,
            executor,
            itemContent);
        var fixture = await CreatePetRebirthFixtureAsync(
            connectionString,
            itemContent);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var rawCorrelation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var secureCorrelation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var hatch = await executor.ExecuteAsync(
            CreateRebirthFixtureHatchEnvelope(
                subject,
                rawCorrelation,
                fixture.EggSlot));
        Check.True(
            hatch.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            hatch.Receipt?.Status == PetDurableReceiptStatus.EggHatched,
            "rebirth fixture hatches one authoritative pet");
        var petId = hatch.Receipt!.PetId;

        await SeedPetRebirthAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var before = await ReadPetRebirthStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);

        var restrictedEnvelope = CreatePetRebirthEnvelope(
            subject,
            secureCorrelation,
            Guid.NewGuid(),
            checked((int)PetItemCatalog.RebornHarpyia));
        var rejected = await executor.ExecuteAsync(restrictedEnvelope);
        var rejectedReplay = await restarted.ExecuteAsync(
            restrictedEnvelope);
        var afterRestrictedRejection = await ReadPetRebirthStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            rejected.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            rejected.Receipt?.Status ==
                PetDurableReceiptStatus
                    .PetRebirthRestrictedRequiresBound &&
            rejectedReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            rejectedReplay.Receipt == rejected.Receipt,
            "restricted rebirth spirit durably rejects an unbound pet");
        AssertPetRebirthValueUnchanged(
            before,
            afterRestrictedRejection,
            "restricted rebirth rejection");

        var operationId = Guid.NewGuid();
        var envelope = CreatePetRebirthEnvelope(
            subject,
            secureCorrelation,
            operationId,
            checked((int)PetItemCatalog.RebirthSpirit));
        var concurrent = await Task.WhenAll(
            executor.ExecuteAsync(envelope),
            restarted.ExecuteAsync(envelope));
        AssertCommitAndDuplicate(
            concurrent,
            PetDurableReceiptStatus.PetReborn,
            "concurrent pet rebirth");
        var receipt = concurrent.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var after = await ReadPetRebirthStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);

        Check.True(
            receipt.PetId == petId &&
            receipt.PetLevel == 1 &&
            receipt.PetExperience == 242_993_145L &&
            receipt.PetRevision == after.PetRevision &&
            after.Level == 1 &&
            after.Experience == 242_993_145L &&
            after.CompletedRebirths == 1 &&
            after.RebirthsRemaining == 0 &&
            after.PetRevision == before.PetRevision + 1 &&
            after.InventoryRevision == before.InventoryRevision + 1 &&
            after.StandardSpiritCount == 0 &&
            after.RestrictedSpiritCount ==
                before.RestrictedSpiritCount &&
            after.SelectedMaterialTemplateId ==
                (int)PetItemCatalog.RebirthSpirit &&
            after.SelectedMaterialQuantity == 5,
            "rebirth resets level, refunds surplus levels, and consumes the split standard stacks exactly once");
        Check.True(
            after.SurplusLevelCount == 90 &&
            after.HistoricalSurplusExperience == 242_980_800L &&
            after.PreRebirthUnspentExperience == 12_345L &&
            after.CarriedExperience == 242_993_145L,
            "rebirth audit separates surplus, unspent, and total carried EXP");
        AssertPetRebirthStats(
            before,
            after,
            receipt.RebirthGrowth);
        Check.True(
            after.RebirthAuditCount == 2 &&
            after.RebirthCommittedAuditCount == 1 &&
            after.RebirthRejectedAuditCount == 1 &&
            after.ConsumedStackCount == 2 &&
            after.ConsumedQuantity ==
                PetRebirthGrowthPolicy.RequiredSpiritCount &&
            after.CommandAuditCount == 2 &&
            after.CommandInboxCount == 2 &&
            after.CommandOutboxCount == 2 &&
            after.InventoryLedgerCount == 2 &&
            after.InventoryReasonCount == 2,
            "rebirth retains command, pet, split-stack inventory, inbox, and outbox evidence");

        var replayed = await restarted.ExecuteAsync(envelope);
        var afterReplay = await ReadPetRebirthStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            replayed.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replayed.Receipt == receipt,
            "rebirth replays its canonical receipt after executor restart");
        AssertPetRebirthStateEqual(
            after with
            {
                DuplicateCount = afterReplay.DuplicateCount
            },
            afterReplay,
            "rebirth replay");

        var conflict = await restarted.ExecuteAsync(
            CreatePetRebirthEnvelope(
                subject,
                secureCorrelation,
                operationId,
                checked((int)PetItemCatalog.RebornHarpyia)));
        var afterConflict = await ReadPetRebirthStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            conflict.Disposition ==
                PetDurableExecutionDisposition.RequestHashConflict &&
            afterConflict.DuplicateCount ==
                afterReplay.DuplicateCount &&
            afterConflict.ConflictCount ==
                afterReplay.ConflictCount + 1,
            "one rebirth operation ID cannot authorize another material template");
        AssertPetRebirthStateEqual(
            afterReplay with
            {
                ConflictCount = afterConflict.ConflictCount
            },
            afterConflict,
            "rebirth hash conflict");
    }

    private static async Task AssertPetRebirthExperienceGatesAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        GameplayItemContent itemContent)
    {
        var cases = new[]
        {
            (Completed: 0, Gate: 30, Surplus: 242_980_800L),
            (Completed: 1, Gate: 80, Surplus: 156_880_350L),
            (Completed: 2, Gate: 100, Surplus: 93_759_075L),
            (Completed: 3, Gate: 110, Surplus: 51_664_650L),
            (Completed: 4, Gate: 120, Surplus: 0L),
            (Completed: 5, Gate: 120, Surplus: 0L),
            (Completed: 99, Gate: 120, Surplus: 0L)
        };
        foreach (var value in cases)
        {
            var fixture = await CreatePetRebirthFixtureAsync(
                connectionString,
                itemContent);
            var subject = new CommandSubject(
                fixture.AccountId,
                fixture.CharacterId);
            var rawCorrelation = new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.LegacyTcp);
            var secureCorrelation = new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureTlsLegacy);
            var hatch = await executor.ExecuteAsync(
                CreateRebirthFixtureHatchEnvelope(
                    subject,
                    rawCorrelation,
                    fixture.EggSlot));
            var petId = hatch.Receipt?.PetId ??
                throw new InvalidDataException(
                    "rebirth-gate fixture failed to hatch.");
            await SeedPetRebirthAsync(
                dataSource,
                fixture.CharacterId,
                petId);

            await using (var command = dataSource.CreateCommand(
                """
                UPDATE public.character_pets
                SET completed_rebirths = @completed,
                    revision = revision + 1,
                    updated_at = transaction_timestamp()
                WHERE id = @petId
                  AND user_id = @characterId;
                """))
            {
                command.Parameters.AddWithValue(
                    "completed",
                    checked((short)value.Completed));
                command.Parameters.AddWithValue("petId", petId);
                command.Parameters.AddWithValue(
                    "characterId",
                    fixture.CharacterId);
                Check.Equal(
                    1,
                    await command.ExecuteNonQueryAsync(),
                    $"rebirth {value.Completed + 1} gate fixture is selected");
            }

            var committed = await executor.ExecuteAsync(
                CreatePetRebirthEnvelope(
                    subject,
                    secureCorrelation,
                    Guid.NewGuid(),
                    materialTemplateId: 0,
                    quantity: 0));
            var after = await ReadPetRebirthStateAsync(
                dataSource,
                fixture.CharacterId,
                petId);
            Check.True(
                committed.Disposition ==
                    PetDurableExecutionDisposition.Committed &&
                committed.Receipt is
                {
                    Status: PetDurableReceiptStatus.PetReborn,
                    PetLevel: 1
                } &&
                committed.Receipt.PetExperience == value.Surplus + 12_345 &&
                after.CompletedRebirths == value.Completed + 1 &&
                after.Experience == value.Surplus + 12_345 &&
                after.SurplusLevelCount == 120 - value.Gate &&
                after.HistoricalSurplusExperience == value.Surplus &&
                after.PreRebirthUnspentExperience == 12_345 &&
                after.CarriedExperience == value.Surplus + 12_345,
                $"rebirth {value.Completed + 1} executor uses active gate {value.Gate}");
        }
    }

    private static async Task AssertZeroSpiritPetRebirthAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        GameplayItemContent itemContent)
    {
        var fixture = await CreatePetRebirthFixtureAsync(
            connectionString,
            itemContent);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var rawCorrelation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var secureCorrelation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var hatch = await executor.ExecuteAsync(
            CreateRebirthFixtureHatchEnvelope(
                subject,
                rawCorrelation,
                fixture.EggSlot));
        var petId = hatch.Receipt?.PetId ??
            throw new InvalidDataException(
                "q0 rebirth fixture failed to hatch.");
        await SeedPetRebirthAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var before = await ReadPetRebirthStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);

        var committed = await executor.ExecuteAsync(
            CreatePetRebirthEnvelope(
                subject,
                secureCorrelation,
                Guid.NewGuid(),
                materialTemplateId: 0,
                quantity: 0));
        var after = await ReadPetRebirthStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);

        Check.True(
            committed.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            committed.Receipt is
            {
                Status: PetDurableReceiptStatus.PetReborn,
                KitBagSlot: -1
            } &&
            after.InventoryRevision == before.InventoryRevision &&
            after.StandardSpiritCount == before.StandardSpiritCount &&
            after.RestrictedSpiritCount ==
                before.RestrictedSpiritCount &&
            after.ConsumedStackCount == 0 &&
            after.ConsumedQuantity == 0 &&
            after.InventoryLedgerCount == 0 &&
            after.InventoryReasonCount == 0 &&
            after.SelectedMaterialTemplateId == 0 &&
            after.SelectedMaterialQuantity == 0,
            "q0 rebirth commits without item or inventory mutation and audits exact wire selection");
        AssertPetRebirthStats(
            before,
            after,
            committed.Receipt?.RebirthGrowth,
            minimum: 0.01m,
            maximum: 0.20m);
    }

    private static async Task AssertPetRebirthCarryOverflowAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        GameplayItemContent itemContent)
    {
        var fixture = await CreatePetRebirthFixtureAsync(
            connectionString,
            itemContent);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var rawCorrelation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var secureCorrelation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var hatch = await executor.ExecuteAsync(
            CreateRebirthFixtureHatchEnvelope(
                subject,
                rawCorrelation,
                fixture.EggSlot));
        var petId = hatch.Receipt?.PetId ??
            throw new InvalidDataException(
                "rebirth-overflow fixture failed to hatch.");
        await SeedPetRebirthAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        await using (var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pets
            SET experience = @experience,
                revision = revision + 1
            WHERE id = @petId AND user_id = @characterId;
            """))
        {
            command.Parameters.AddWithValue(
                "experience",
                PetExperienceItemPolicy.MaximumNativePetExperience);
            command.Parameters.AddWithValue("petId", petId);
            command.Parameters.AddWithValue(
                "characterId",
                fixture.CharacterId);
            Check.Equal(
                1,
                await command.ExecuteNonQueryAsync(),
                "rebirth overflow fixture reaches the native EXP ceiling");
        }
        var before = await ReadPetRebirthStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);

        var result = await executor.ExecuteAsync(
            CreatePetRebirthEnvelope(
                subject,
                secureCorrelation,
                Guid.NewGuid(),
                checked((int)PetItemCatalog.RebirthSpirit)));
        var after = await ReadPetRebirthStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);

        Check.True(
            result.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            result.Receipt?.Status ==
                PetDurableReceiptStatus.PetRebirthInvalidState &&
            after.RebirthRejectedAuditCount ==
                before.RebirthRejectedAuditCount + 1,
            "rebirth carry overflow is durably rejected");
        AssertPetRebirthValueUnchanged(
            before,
            after,
            "rebirth carry overflow");
    }
}
