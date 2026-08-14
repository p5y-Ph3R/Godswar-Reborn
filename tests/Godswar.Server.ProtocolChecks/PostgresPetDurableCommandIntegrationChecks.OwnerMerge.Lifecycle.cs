using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertActiveOwnerMergeLifecycleAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        PetFixture fixture,
        CommandSubject subject,
        CommandConnectionCorrelation correlation,
        long petId,
        OwnerMergePersistenceState before,
        OwnerMergePersistenceState merged,
        OwnerMergeProjectedStats beforeStats,
        GameplayItemContent itemContent)
    {
        var toggleOffEnvelope = CreateOwnerMergeToggleEnvelope(
            subject,
            correlation,
            Guid.NewGuid());
        var toggledOff = await restarted.ExecuteAsync(toggleOffEnvelope);
        Check.True(
            toggledOff.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            toggledOff.Receipt?.Status ==
                PetDurableReceiptStatus.OwnerUnmerged,
            "a second native Merge action toggles the state off");
        var afterToggleOff = await ReadOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            !afterToggleOff.Contributes &&
            afterToggleOff.PetRevision == merged.PetRevision + 1 &&
            afterToggleOff.BonusCount == 0 &&
            afterToggleOff.AuditCount == merged.AuditCount + 1,
            "Merge toggle-off removes every temporary contribution atomically");

        var toggledOn = await executor.ExecuteAsync(
            CreateOwnerMergeToggleEnvelope(
                subject,
                correlation,
                Guid.NewGuid()));
        Check.True(
            toggledOn.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            toggledOn.Receipt?.Status == PetDurableReceiptStatus.OwnerMerged,
            "a later native Merge action starts a fresh active lifetime");
        var activeMerge = await ReadOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            activeMerge.Contributes && activeMerge.BonusCount == 16,
            "fresh Merge restores all 16 contribution rows");

        await SeedOwnerMergePhoenixFeatherAsync(
            dataSource,
            fixture.CharacterId);
        var progressionBefore = await ReadPetGrowthResetStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var bonusRowsBefore = await ReadReconciledOwnerMergeRowsAsync(
            dataSource,
            petId);
        var blockedLevel = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                PetLevelUpgradeCommandEnvelope.CreateRawLocal(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    new PetLevelUpgradeCommand(
                        PetCommandOperationIdentity.RawLocalServer(
                            Guid.NewGuid(),
                            correlation.ConnectionId),
                        petId))));
        var blockedGrowth = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                PetGrowthResetCommandEnvelope.CreateRawLocal(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    new PetGrowthResetCommand(
                        PetCommandOperationIdentity.RawLocalServer(
                            Guid.NewGuid(),
                            correlation.ConnectionId)))));
        var blockedRebirth = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                PetRebirthCommandEnvelope.CreateRawLocal(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    new PetRebirthCommand(
                        PetCommandOperationIdentity.RawLocalServer(
                            Guid.NewGuid(),
                            correlation.ConnectionId),
                        checked((int)PetItemCatalog.RebirthSpirit),
                        PetRebirthGrowthPolicy.RequiredSpiritCount))));
        var progressionAfter = await ReadPetGrowthResetStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var bonusRowsAfter = await ReadReconciledOwnerMergeRowsAsync(
            dataSource,
            petId);
        Check.True(
            blockedLevel.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            blockedLevel.Receipt?.Status ==
                PetDurableReceiptStatus.PetUnavailable &&
            blockedGrowth.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            blockedGrowth.Receipt?.Status ==
                PetDurableReceiptStatus.PetNotTaken &&
            blockedRebirth.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            blockedRebirth.Receipt?.Status ==
                PetDurableReceiptStatus.PetRebirthInvalidState,
            "owner Merge rejects level, Phoenix, and rebirth stat mutation");
        Check.True(
            SameGrowthResetState(progressionAfter, progressionBefore) &&
            bonusRowsAfter.SequenceEqual(bonusRowsBefore),
            "blocked owner-Merge progression preserves pet stats, feather, inventory, and bonuses");

        foreach (var operation in Enum.GetValues<
                     PetPresenceCommandOperation>())
        {
            var blockedPresence = await executor.ExecuteAsync(
                PlayerOwnershipTestFences.Bind(
                    PetPresenceTransitionCommandEnvelope.CreateRawLocal(
                        subject,
                        correlation,
                        DateTimeOffset.UtcNow,
                        new PetPresenceTransitionCommand(
                            PetCommandOperationIdentity.RawLocalServer(
                                Guid.NewGuid(),
                                correlation.ConnectionId),
                            petId,
                            operation))));
            Check.True(
                blockedPresence.Disposition ==
                    PetDurableExecutionDisposition.TerminalRejected &&
                blockedPresence.Receipt?.Status ==
                    PetDurableReceiptStatus.PetUnavailable,
                $"{operation} is rejected for the full owner-Merge lifetime");
        }
        var afterBlockedPresence = await ReadOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            afterBlockedPresence.Contributes &&
            afterBlockedPresence.PetRevision == activeMerge.PetRevision &&
            afterBlockedPresence.BonusCount == 16,
            "blocked pet presence actions preserve every Merge contribution");

        var ownership = PlayerOwnershipTestFences.ForCharacter(
            fixture.CharacterId);
        var drained = await executor.DrainEnergyAsync(
            subject,
            ownership,
            energyPoints: 1);
        var afterDrain = await ReadOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            drained.Status ==
                PetOwnerMergeLifecycleStatus.EnergyChanged &&
            drained.CurrentEnergy == activeMerge.CurrentEnergy - 1 &&
            afterDrain.Contributes &&
            afterDrain.CurrentEnergy == drained.CurrentEnergy &&
            afterDrain.PetRevision == activeMerge.PetRevision + 1 &&
            afterDrain.BonusCount == 16,
            "one deterministic online settlement spends one energy and preserves all contribution rows");

        var expired = await restarted.DrainEnergyAsync(
            subject,
            ownership,
            energyPoints: int.MaxValue);
        var afterExpiry = await ReadOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            expired.Status ==
                PetOwnerMergeLifecycleStatus.MergeEnded &&
            expired.CurrentEnergy == 0 &&
            !afterExpiry.Contributes &&
            afterExpiry.CurrentEnergy == 0 &&
            afterExpiry.PetRevision == afterDrain.PetRevision + 1 &&
            afterExpiry.BonusCount == 0 &&
            afterExpiry.InventoryRevision == before.InventoryRevision &&
            afterExpiry.AuditCount ==
                activeMerge.AuditCount + 1 &&
            afterExpiry.CommittedAuditCount == 4,
            "energy expiry atomically removes the flag and all 16 contribution rows without changing inventory");
        var unmergedStats = await ReadProjectedMergeStatsAsync(
            connectionString,
            fixture,
            itemContent);
        Check.Equal(
            beforeStats,
            unmergedStats,
            "energy expiry restores every normal calculated character stat");

        await AssertExplicitOwnerMergeEndAsync(
            dataSource,
            executor,
            subject,
            correlation,
            fixture.CharacterId,
            petId,
            ownership,
            PetOwnerMergeEndReason.SessionEnded);
        await AssertExplicitOwnerMergeEndAsync(
            dataSource,
            restarted,
            subject,
            correlation,
            fixture.CharacterId,
            petId,
            ownership,
            PetOwnerMergeEndReason.StaleLoginRecovery);
    }

    private static async Task SeedOwnerMergePhoenixFeatherAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, 89, 11005,
                1, 1, 1, 1, 0, 0
            );
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "owner-Merge progression guard fixture inserts one Phoenix Feather");
    }

    private static async Task AssertExplicitOwnerMergeEndAsync(
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        CommandSubject subject,
        CommandConnectionCorrelation correlation,
        int characterId,
        long petId,
        PlayerOwnershipFence ownership,
        PetOwnerMergeEndReason reason)
    {
        await using (var refill = dataSource.CreateCommand(
            """
            UPDATE public.character_pets
            SET current_energy = maximum_energy,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId;
            """))
        {
            refill.Parameters.AddWithValue("petId", petId);
            refill.Parameters.AddWithValue("characterId", characterId);
            Check.Equal(
                1,
                await refill.ExecuteNonQueryAsync(),
                $"{reason} fixture refills pet energy");
        }

        var merge = await executor.ExecuteAsync(
            CreateOwnerMergeToggleEnvelope(
                subject,
                correlation,
                Guid.NewGuid()));
        Check.True(
            merge.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            merge.Receipt?.Status == PetDurableReceiptStatus.OwnerMerged,
            $"{reason} fixture starts one Merge");

        var ended = await executor.EndAsync(
            subject,
            ownership,
            reason);
        var state = await ReadOwnerMergeStateAsync(
            dataSource,
            characterId,
            petId);
        Check.True(
            ended.Status == PetOwnerMergeLifecycleStatus.MergeEnded &&
            !state.Contributes &&
            state.BonusCount == 0,
            $"{reason} atomically clears active Merge and its projections");
    }
}
