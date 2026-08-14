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
    public const string ConsumableCooldownCheckName =
        "PostgreSQL authoritative bag-consumable cooldown";

    public static async Task RunConsumableCooldownOnlyAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {ConsumableCooldownCheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            Console.WriteLine(
                $"SKIP {ConsumableCooldownCheckName} requires a " +
                $"disposable B03/B12 database; received '{database}'");
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
        var restarted = new PostgresPetDurableCommandExecutor(
            dataSource,
            options,
            itemContent,
            petContent,
            ownerMergeContent,
            PetLearnedSkillContentBaseline.Create(),
            new ThrowingPetHatchRankRollSource());
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var hatchEnvelope = PlayerOwnershipTestFences.Bind(
            BagItemActivationCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new BagItemActivationCommand(
                    PetCommandOperationIdentity.RawLocalServer(
                        Guid.NewGuid(),
                        correlation.ConnectionId),
                    fixture.EggSlot)));
        var hatch = await executor.ExecuteAsync(hatchEnvelope);
        Check.True(
            hatch is
            {
                Disposition: PetDurableExecutionDisposition.Committed,
                Receipt.Status: PetDurableReceiptStatus.EggHatched
            },
            "cooldown fixture hatches one carried pet");

        await AssertPetExperienceItemsAsync(
            dataSource,
            executor,
            restarted,
            subject,
            correlation,
            hatch.Receipt!.PetId);
    }
}
