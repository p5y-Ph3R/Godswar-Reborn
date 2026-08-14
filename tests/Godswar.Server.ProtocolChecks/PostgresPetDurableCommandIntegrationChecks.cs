using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL retry-safe pet value commands";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b12_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            Console.WriteLine(
                $"SKIP {CheckName} requires a disposable B03/B12 " +
                $"database; received '{database}'");
            return;
        }

        await new PostgresSchemaMigrationRunner(dataSource)
            .InitializeGodswarSchemaAsync();

        GameplayItemContent itemContent;
        IPetContentCatalog petContent;
        await using (var store =
                      new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
            itemContent = store.ItemContent;
            petContent = store.PetContent;
        }
        var fixture = await CreateFixtureAsync(connectionString);
        var options = new PostgresOutboxDispatcherOptions();
        var ownerMergeContent =
            await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(
                dataSource);
        var executor = new PostgresPetDurableCommandExecutor(
            dataSource,
            options,
            itemContent,
            petContent,
            ownerMergeContent,
            PetLearnedSkillContentBaseline.Create(),
            new FixedPetHatchRankRollSource(89));
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var rawCorrelation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);

        var hatchOperation = Guid.NewGuid();
        var hatchEnvelope = PlayerOwnershipTestFences.Bind(
            BagItemActivationCommandEnvelope.CreateRawLocal(
                subject,
                rawCorrelation,
                DateTimeOffset.UtcNow,
                new BagItemActivationCommand(
                    PetCommandOperationIdentity.RawLocalServer(
                        hatchOperation,
                        rawCorrelation.ConnectionId),
                    fixture.EggSlot)));
        var concurrentHatch = await Task.WhenAll(
            executor.ExecuteAsync(hatchEnvelope),
            executor.ExecuteAsync(hatchEnvelope));
        AssertCommitAndDuplicate(
            concurrentHatch,
            PetDurableReceiptStatus.EggHatched,
            "concurrent hatch");
        var hatchReceipt = concurrentHatch.Single(
            result => result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        Check.True(
            hatchReceipt.HatchRank == new PetHatchRankEvidence(
                0.80m,
                OutcomeOrder: 1,
                Roll: 89,
                petContent.Revision.Sha256),
            "durable Calm hatch receipt retains its deterministic rank roll and source revision");
        var randomState = await ReadHatchStateAsync(
            dataSource,
            fixture,
            hatchReceipt.PetId);
        Check.True(
            randomState.PetCount == 1 &&
            randomState.PetShedCapacity == 2 &&
            randomState.PetShedRevision == 0 &&
            randomState.EggCount == 0 &&
            randomState.Aptitude == (short)PetAptitude.Calm &&
            randomState.OpenedSkillSlots == 1 &&
            randomState.AvailableSkillSlots == 1 &&
            randomState.LearnedSkillCount == 1 &&
            randomState.IsCarried &&
            !randomState.IsSummoned &&
            randomState.TalentMask == 0 &&
            !randomState.HasOwnerMergeTalent &&
            randomState.Rank == 0.80m &&
            randomState.BirthRank == 0.80m &&
            randomState.HatchRankRoll == 89 &&
            randomState.HatchRankOutcomeOrder == 1 &&
            randomState.HatchRankContentRevision ==
                petContent.Revision.Sha256 &&
            randomState.StatValues.Length == 6,
            "Calm hatch atomically persists its approved rank evidence with the complete pet");
        await AssertDurableHatchEvidenceAsync(
            dataSource,
            hatchReceipt);
        await AssertDurableHatchEvidenceSurvivesPetDeletionAsync(
            dataSource,
            hatchReceipt);
        var hatchInventory = await ReadInventoryStateAsync(
            dataSource,
            fixture.CharacterId);
        Check.True(
            hatchInventory.InventoryRevision == 1 &&
            hatchInventory.LedgerEntryCount == 1 &&
            hatchInventory.OutboxCount == 1 &&
            hatchInventory.IsReconciled &&
            hatchInventory.OutboxVersions.SequenceEqual(
                new long[] { 1 }),
            "hatch delete retains one reconciled inventory revision");

        var restarted = new PostgresPetDurableCommandExecutor(
            dataSource,
            options,
            itemContent,
            petContent,
            ownerMergeContent,
            PetLearnedSkillContentBaseline.Create(),
            new ThrowingPetHatchRankRollSource());
        var restartHatch = await restarted.ExecuteAsync(hatchEnvelope);
        Check.True(
            restartHatch.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            restartHatch.Receipt == hatchReceipt,
            "hatch replays its exact receipt after executor restart");
        var replayedRandomState = await ReadHatchStateAsync(
            dataSource,
            fixture,
            hatchReceipt.PetId);
        Check.True(
            replayedRandomState.PetCount == randomState.PetCount &&
            replayedRandomState.EggCount == randomState.EggCount &&
            replayedRandomState.StatValues.SequenceEqual(
                randomState.StatValues) &&
            replayedRandomState.Rank == randomState.Rank &&
            replayedRandomState.BirthRank == randomState.BirthRank &&
            replayedRandomState.HatchRankRoll ==
                randomState.HatchRankRoll &&
            replayedRandomState.HatchRankContentRevision ==
                randomState.HatchRankContentRevision,
            "hatch replay preserves its random aptitude/stat/rank outcome without rerolling");

        var conflict = await restarted.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                BagItemActivationCommandEnvelope.CreateRawLocal(
                    subject,
                    rawCorrelation,
                    DateTimeOffset.UtcNow,
                    new BagItemActivationCommand(
                        PetCommandOperationIdentity.RawLocalServer(
                            hatchOperation,
                            rawCorrelation.ConnectionId),
                        fixture.EggSlot - 1))));
        Check.True(
            conflict.Disposition ==
                PetDurableExecutionDisposition.RequestHashConflict,
            "one hatch UUID cannot authorize another bag slot");

        var equipmentBagSlot = await PrepareEquipmentActivationAsync(
            dataSource,
            fixture);
        var equipEnvelope = PlayerOwnershipTestFences.Bind(
            BagItemActivationCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new BagItemActivationCommand(
                    Guid.NewGuid(),
                    equipmentBagSlot)));
        var equipped = await executor.ExecuteAsync(equipEnvelope);
        var replayedEquip = await restarted.ExecuteAsync(equipEnvelope);
        Check.True(
            equipped.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            equipped.Receipt?.Status ==
                PetDurableReceiptStatus.EquipmentEquipped &&
            replayedEquip.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replayedEquip.Receipt == equipped.Receipt,
            "equipment activation commits once and replays exactly");
        var equipInventory = await ReadInventoryStateAsync(
            dataSource,
            fixture.CharacterId);
        Check.True(
            equipInventory.InventoryRevision == 2 &&
            equipInventory.LedgerEntryCount == 2 &&
            equipInventory.OutboxCount == 2 &&
            equipInventory.IsReconciled &&
            equipInventory.OutboxVersions.SequenceEqual(
                new long[] { 1, 2 }),
            "equipment activation retains a second reconciled revision");

        await SeedPetExperienceAsync(
            dataSource,
            hatchReceipt.PetId,
            3_000);
        var levelOperation = Guid.NewGuid();
        var levelEnvelope = PlayerOwnershipTestFences.Bind(
            PetLevelUpgradeCommandEnvelope.CreateRawLocal(
                subject,
                rawCorrelation,
                DateTimeOffset.UtcNow,
                new PetLevelUpgradeCommand(
                    PetCommandOperationIdentity.RawLocalServer(
                        levelOperation,
                        rawCorrelation.ConnectionId),
                    hatchReceipt.PetId)));
        var concurrentLevel = await Task.WhenAll(
            executor.ExecuteAsync(levelEnvelope),
            executor.ExecuteAsync(levelEnvelope));
        AssertCommitAndDuplicate(
            concurrentLevel,
            PetDurableReceiptStatus.PetLevelUpgraded,
            "concurrent level-up");
        var levelReceipt = concurrentLevel.Single(
            result => result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        Check.True(
            levelReceipt.PetLevel == 2 &&
            levelReceipt.PetExperience == 1_500,
            "pet level-up spends the level-one cost exactly once");
        var levelState = await ReadLevelStateAsync(
            dataSource,
            hatchReceipt.PetId);
        Check.True(
            levelState.HasExactScaledAdded,
            "pet level-up materializes Added as effective Growth times level");
        var restartLevel = await restarted.ExecuteAsync(levelEnvelope);
        var replayedLevelState = await ReadLevelStateAsync(
            dataSource,
            hatchReceipt.PetId);
        Check.True(
            restartLevel.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            restartLevel.Receipt == levelReceipt &&
            replayedLevelState.Level == levelState.Level &&
            replayedLevelState.Experience == levelState.Experience &&
            replayedLevelState.Revision == levelState.Revision &&
            replayedLevelState.HasExactScaledAdded ==
                levelState.HasExactScaledAdded &&
            replayedLevelState.StatValues.SequenceEqual(
                levelState.StatValues),
            "pet level replay cannot spend experience or grow stats twice");

        var take = await CheckPresenceReplayAsync(
            executor,
            restarted,
            subject,
            rawCorrelation,
            hatchReceipt.PetId,
            PetPresenceCommandOperation.Take,
            isCarried: true,
            isSummoned: false);
        var callOut = await CheckPresenceReplayAsync(
            executor,
            restarted,
            subject,
            rawCorrelation,
            hatchReceipt.PetId,
            PetPresenceCommandOperation.CallOut,
            isCarried: true,
            isSummoned: true);
        var recall = await CheckPresenceReplayAsync(
            executor,
            restarted,
            subject,
            rawCorrelation,
            hatchReceipt.PetId,
            PetPresenceCommandOperation.Recall,
            isCarried: true,
            isSummoned: false);
        var delayedCallOut =
            await restarted.ExecuteAsync(callOut.Envelope);
        var currentPresence = await ReadPetPresenceAsync(
            dataSource,
            hatchReceipt.PetId);
        Check.True(
            delayedCallOut.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            delayedCallOut.Receipt == callOut.Receipt &&
            currentPresence ==
                (IsCarried: true, IsSummoned: false) &&
            GameClientHandler.ResolveAuthoritativePresenceResult(
                callOut.Receipt,
                currentPetExists: true,
                currentPresence.IsCarried,
                currentPresence.IsSummoned) ==
                PetOperationResultCode.RecallSucceeded,
            "CallOut then Recall then old CallOut retry preserves and presents the current recalled projection");

        var expectedAuditIds = new[]
        {
            hatchReceipt,
            equipped.Receipt!,
            levelReceipt,
            take.Receipt,
            callOut.Receipt,
            recall.Receipt
        }.Select(static receipt =>
            long.TryParse(receipt.AuditReference, out var auditId)
                ? auditId
                : throw new InvalidDataException(
                    "Pet receipt has an invalid audit reference."))
            .ToArray();
        var dispatcher = new PostgresOutboxDispatcher(
            dataSource,
            new IOutboxEventConsumer[]
            {
                new PetDurableOutboxConsumer(),
                new CharacterInventoryOutboxConsumer()
            },
            options,
            "checks-b12-pet-inventory");
        for (var pass = 0; pass < 8; pass++)
        {
            await dispatcher.DispatchOnceAsync();
            var state = await ReadInventoryStateAsync(
                dataSource,
                fixture.CharacterId);
            var petEvidence = await ReadEvidenceAsync(
                dataSource,
                fixture.CharacterId,
                expectedAuditIds);
            if (state.DeliveredOutboxCount == 2 &&
                state.PositionVersion == 2 &&
                petEvidence.PositionVersion == 6)
            {
                break;
            }
        }

        var evidence = await ReadEvidenceAsync(
            dataSource,
            fixture.CharacterId,
            expectedAuditIds);
        Check.True(
            evidence is
            {
                StreamVersion: 6,
                AuditCount: 6,
                InboxCount: 6,
                OutboxCount: 6,
                PositionVersion: 6
            } &&
            evidence.OutboxVersions.SequenceEqual(
                new long[] { 1, 2, 3, 4, 5, 6 }) &&
            evidence.DuplicateCount == 9 &&
            evidence.ConflictCount == 1,
            "pet commands retain contiguous audit/inbox/outbox evidence");
        var dispatchedInventory = await ReadInventoryStateAsync(
            dataSource,
            fixture.CharacterId);
        Check.True(
            dispatchedInventory is
            {
                InventoryRevision: 2,
                LedgerEntryCount: 2,
                OutboxCount: 2,
                DeliveredOutboxCount: 2,
                PositionVersion: 2,
                IsReconciled: true
            },
            "pet bag inventory events advance the strict checkpoint");

        await AssertPetShedExpansionAsync(
            dataSource,
            executor,
            restarted,
            subject,
            rawCorrelation);

        await AssertPetSkillCellItemsAsync(
            dataSource,
            executor,
            restarted,
            subject,
            rawCorrelation,
            hatchReceipt.PetId);

        await AssertPetGrowthResetAsync(
            dataSource,
            executor,
            restarted,
            subject,
            rawCorrelation,
            hatchReceipt.PetId);

        await AssertPetBasicSavvyResetAsync(
            dataSource,
            executor,
            restarted,
            subject,
            rawCorrelation,
            hatchReceipt.PetId);

        await AssertPetSkillUnlearnAsync(
            dataSource,
            executor,
            restarted,
            subject,
            rawCorrelation,
            hatchReceipt.PetId);

        await AssertPetExperienceItemsAsync(
            dataSource,
            executor,
            restarted,
            subject,
            rawCorrelation,
            hatchReceipt.PetId);

        await AssertTakeSwitchesCompanionAtomicallyAsync(
            dataSource,
            executor,
            restarted,
            subject,
            rawCorrelation,
            hatchReceipt.PetId);

        await AssertAuthoritativeEquipmentEligibilityAsync(
            dataSource,
            itemContent);
        await AssertRawPostgresMutationsFailClosedAsync(
            connectionString,
            fixture,
            hatchReceipt.PetId);
        await AssertStreamProjectionDoesNotBlockPurgeAsync(
            dataSource,
            fixture.CharacterId);
        await AssertOwnerMergeAsync(
            connectionString,
            dataSource,
            executor,
            restarted,
            itemContent,
            ownerMergeContent);
        await AssertOwnerMergeRevisionSafetyAsync(
            connectionString,
            dataSource,
            executor,
            itemContent,
            ownerMergeContent);
        await AssertOwnerMergeRevisionReconciliationAsync(
            connectionString,
            dataSource,
            executor,
            itemContent,
            ownerMergeContent);
        await AssertPetToPetMergeAsync(
            connectionString,
            dataSource,
            executor,
            restarted,
            itemContent);
        await AssertPetRebirthAsync(
            connectionString,
            dataSource,
            executor,
            restarted,
            itemContent);
        await AssertPetAppearanceChangeAsync(
            connectionString, dataSource, executor, restarted,
            itemContent, petContent);
        await AssertPetSoulContractAsync(
            connectionString,
            dataSource,
            executor,
            restarted);
        await AssertPetManagerUtilityAsync(
            connectionString,
            dataSource,
            executor,
            restarted,
            itemContent);
    }

}
