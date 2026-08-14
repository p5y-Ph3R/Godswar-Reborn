using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    public const string HatchRankCheckName =
        "PostgreSQL durable pet hatch-rank evidence";

    public static async Task RunHatchRankOnlyAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {HatchRankCheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            Console.WriteLine(
                $"SKIP {HatchRankCheckName} requires a disposable " +
                $"B03/B12 database; received '{database}'");
            return;
        }

        await new PostgresSchemaMigrationRunner(dataSource)
            .InitializeGodswarSchemaAsync();

        GameplayItemContent itemContent;
        IPetContentCatalog petContent;
        await using (var store = new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
            itemContent = store.ItemContent;
            petContent = store.PetContent;
        }

        var fixture = await CreateFixtureAsync(connectionString);
        var ownerMergeContent =
            await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(
                dataSource);
        var options = new PostgresOutboxDispatcherOptions();
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
            CommandTransportKind.LegacyTcp);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var envelope = PlayerOwnershipTestFences.Bind(
            BagItemActivationCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new BagItemActivationCommand(
                    PetCommandOperationIdentity.RawLocalServer(
                        Guid.NewGuid(),
                        correlation.ConnectionId),
                    fixture.EggSlot)));

        var results = await Task.WhenAll(
            executor.ExecuteAsync(envelope),
            executor.ExecuteAsync(envelope));
        AssertCommitAndDuplicate(
            results,
            PetDurableReceiptStatus.EggHatched,
            "durable hatch rank");
        var receipt = results.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var expected = new PetHatchRankEvidence(
            Rank: 0.80m,
            OutcomeOrder: 1,
            Roll: 89,
            ContentRevision: petContent.Revision.Sha256);
        Check.True(
            receipt.HatchRank == expected,
            "durable hatch receipt retains the pinned rank decision");

        var state = await ReadHatchStateAsync(
            dataSource,
            fixture,
            receipt.PetId);
        Check.True(
            state.Rank == expected.Rank &&
            state.BirthRank == expected.Rank &&
            state.HatchRankRoll == expected.Roll &&
            state.HatchRankOutcomeOrder == expected.OutcomeOrder &&
            state.HatchRankContentRevision == expected.ContentRevision,
            "durable hatch transaction atomically writes current, birth, roll, outcome, and source revision");
        await AssertDurableHatchEvidenceAsync(dataSource, receipt);
        await AssertDurableHatchEvidenceSurvivesPetDeletionAsync(
            dataSource,
            receipt);

        var restarted = new PostgresPetDurableCommandExecutor(
            dataSource,
            options,
            itemContent,
            petContent,
            ownerMergeContent,
            PetLearnedSkillContentBaseline.Create(),
            new ThrowingPetHatchRankRollSource());
        var replay = await restarted.ExecuteAsync(envelope);
        Check.True(
            replay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replay.Receipt == receipt,
            "durable hatch replay returns exact rank evidence without rerolling");
    }
}
