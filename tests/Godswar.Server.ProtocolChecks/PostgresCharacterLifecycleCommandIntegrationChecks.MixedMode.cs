using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresCharacterLifecycleCommandIntegrationChecks
{
    private static async Task AssertBackfilledFirstEventDispatchesAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresCharacterLifecycleCommandExecutor executor,
        CommandConnectionCorrelation correlation,
        string token)
    {
        GameAccount migratedAccount;
        GameCharacter migratedCharacter;
        await using (var store =
                     new PostgresGameStore(connectionString))
        {
            migratedAccount = await store.LoginOrCreateAccountAsync(
                $"b11_migrated_{token}",
                string.Empty);
            migratedCharacter = await store.CreateCharacterAsync(
                migratedAccount.Id,
                new GameCharacter
                {
                    Name = $"Migrated{token}",
                    Camp = GameDefaults.SpartaCamp,
                    Profession = 0,
                    Level = 1
                });
        }

        await using (var checkpointStore =
                     new PostgresCharacterCheckpointStore(dataSource))
        {
            var ownership = await checkpointStore.AcquireAsync(
                migratedAccount.Id,
                migratedCharacter.Id,
                Guid.NewGuid()) ??
                throw new InvalidOperationException(
                    "Broad lifecycle ownership fixture is missing.");
            await using (var store =
                         new PostgresGameStore(connectionString))
            {
                Check.True(
                    !await store.DeleteCharacterAsync(
                        migratedAccount.Id,
                        migratedCharacter.Name),
                    "broad delete cannot evict an actively owned character");
            }
            Check.Equal(
                (int)CharacterCheckpointReleaseStatus.Released,
                (int)await checkpointStore.ReleaseAsync(
                    migratedAccount.Id,
                    migratedCharacter.Id,
                    ownership.Owner),
                "broad lifecycle fixture releases its player owner");
        }

        var deletion = await executor.ExecuteAsync(
            DeleteEnvelope(
                migratedAccount.Id,
                correlation,
                new CharacterDeleteCommand(
                    Guid.NewGuid(),
                    0,
                    migratedCharacter.Name,
                    migratedCharacter.Id,
                    1)));
        Check.Equal(
            2L,
            deletion.Receipt!.LifecycleVersion,
            "broad creation before stream emits first secure revision two");
        var before = await ReadOutboxDispatchStateAsync(
            dataSource,
            migratedAccount.Id,
            deletion.Receipt.OutboxEventId!.Value);
        Check.True(
            before.CurrentVersion == 1 &&
            !before.Delivered,
            "executor seeds migrated strict stream at prior revision one");

        var dispatcher = new PostgresOutboxDispatcher(
            dataSource,
            [new CharacterLifecycleOutboxConsumer()],
            new PostgresOutboxDispatcherOptions(),
            "checks-character-lifecycle");
        await AssertDispatchedAsync(
            dispatcher,
            dataSource,
            migratedAccount.Id,
            deletion.Receipt,
            2,
            "first secure delete");

        await AssertBroadMutationRejectedAsync(
            async () =>
            {
                await using var store =
                    new PostgresGameStore(connectionString);
                _ = await store.CreateCharacterAsync(
                    migratedAccount.Id,
                    new GameCharacter
                    {
                        Name = $"Blocked{token}",
                        Camp = GameDefaults.SpartaCamp,
                        Profession = 0,
                        Level = 1
                    });
            },
            "broad create is rejected after secure stream starts");

        var replacementName = $"Durable{token}";
        var creation = await executor.ExecuteAsync(
            CreateEnvelope(
                migratedAccount.Id,
                correlation,
                Guid.NewGuid(),
                replacementName));
        Check.True(
            creation.IsSuccess &&
            creation.Receipt!.LifecycleVersion == 3,
            "broad create rejection consumes no lifecycle revision");
        await AssertDispatchedAsync(
            dispatcher,
            dataSource,
            migratedAccount.Id,
            creation.Receipt!,
            3,
            "secure replacement create");

        await AssertBroadMutationRejectedAsync(
            async () =>
            {
                await using var store =
                    new PostgresGameStore(connectionString);
                _ = await store.DeleteCharacterAsync(
                    migratedAccount.Id,
                    replacementName);
            },
            "broad delete is rejected after secure stream starts");

        var finalDeletion = await executor.ExecuteAsync(
            DeleteEnvelope(
                migratedAccount.Id,
                correlation,
                new CharacterDeleteCommand(
                    Guid.NewGuid(),
                    0,
                    replacementName,
                    creation.Receipt!.CharacterId,
                    creation.Receipt.LifecycleVersion)));
        Check.True(
            finalDeletion.IsSuccess &&
            finalDeletion.Receipt!.LifecycleVersion == 4,
            "broad delete rejection consumes no lifecycle revision");
        await AssertDispatchedAsync(
            dispatcher,
            dataSource,
            migratedAccount.Id,
            finalDeletion.Receipt!,
            4,
            "secure replacement delete");
    }

    private static async Task AssertDispatchedAsync(
        PostgresOutboxDispatcher dispatcher,
        NpgsqlDataSource dataSource,
        int accountId,
        CharacterLifecycleReceipt receipt,
        long expectedVersion,
        string description)
    {
        Check.True(
            await dispatcher.DispatchOnceAsync() > 0,
            $"{description} is dispatched");
        var state = await ReadOutboxDispatchStateAsync(
            dataSource,
            accountId,
            receipt.OutboxEventId!.Value);
        Check.True(
            state.CurrentVersion == expectedVersion &&
            state.Delivered,
            $"{description} advances a contiguous strict stream");
    }

    private static async Task AssertBroadMutationRejectedAsync(
        Func<Task> action,
        string description)
    {
        try
        {
            await action();
            throw new InvalidOperationException(
                $"Expected broad lifecycle rejection: {description}.");
        }
        catch (CharacterLifecycleDurableStreamActiveException)
        {
        }
    }
}
