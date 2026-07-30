using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Talents;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresTalentInboxOutboxIntegrationChecks
{
    private static async Task AssertOwnershipFencingAsync(
        string connectionString)
    {
        await AssertForeignOwnerCannotMutateAsync(connectionString);
        await AssertStaleOwnerCannotReplayAsync(connectionString);
    }

    private static async Task AssertForeignOwnerCannotMutateAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "owner",
            level: 80,
            talentPoints: 100);
        var currentEnvelope = CreateEnvelope(
            fixture,
            expectedRank: 0);
        var foreignEnvelope = currentEnvelope with
        {
            Ownership = currentEnvelope.Ownership with
            {
                OwnerId = Guid.NewGuid()
            }
        };
        var before = await ReadStateAsync(connectionString, fixture);

        await using var source =
            NpgsqlDataSource.Create(connectionString);
        await AssertOwnershipLostAsync(
            () => CreateExecutor(source).ExecuteAsync(foreignEnvelope),
            "a foreign owner is rejected before talent mutation");

        var after = await ReadStateAsync(connectionString, fixture);
        Check.Equal(
            before,
            after,
            "foreign ownership rejection leaves value, inbox, audit, " +
            "and outbox state unchanged");
    }

    private static async Task AssertStaleOwnerCannotReplayAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "stale",
            level: 80,
            talentPoints: 100);
        var staleEnvelope = CreateEnvelope(
            fixture,
            expectedRank: 0);

        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var committed = await CreateExecutor(source)
            .ExecuteAsync(staleEnvelope);
        RequireReceipt(
            committed,
            TalentUpgradeExecutionDisposition.Committed,
            "ownership replay fixture commit");
        var beforeReplay =
            await ReadStateAsync(connectionString, fixture);

        await ReplaceOwnerAsync(
            connectionString,
            fixture,
            staleEnvelope.Ownership);
        await AssertOwnershipLostAsync(
            () => CreateExecutor(source).ExecuteAsync(staleEnvelope),
            "a stale owner is rejected before durable replay");

        var afterReplay =
            await ReadStateAsync(connectionString, fixture);
        Check.Equal(
            beforeReplay,
            afterReplay,
            "stale replay rejection does not increment duplicate evidence " +
            "or change committed value state");
    }

    private static async Task ReplaceOwnerAsync(
        string connectionString,
        TalentFixture fixture,
        PlayerOwnershipFence staleOwner)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET checkpoint_owner_id = @replacementOwnerId,
                checkpoint_owner_generation =
                    checkpoint_owner_generation + 1
            WHERE id = @characterId
              AND account_id = @accountId
              AND checkpoint_owner_id = @staleOwnerId
              AND checkpoint_owner_generation = @staleGeneration
              AND lifecycle_state = 'active';
            """,
            connection);
        command.Parameters.AddWithValue(
            "replacementOwnerId",
            Guid.NewGuid());
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "accountId",
            fixture.AccountId);
        command.Parameters.AddWithValue(
            "staleOwnerId",
            staleOwner.OwnerId);
        command.Parameters.AddWithValue(
            "staleGeneration",
            staleOwner.Generation);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "ownership replay fixture transfers the durable owner once");
    }

    private static async Task AssertOwnershipLostAsync(
        Func<Task> action,
        string description)
    {
        try
        {
            await action();
        }
        catch (PlayerOwnershipValidationException error)
            when (error.Status ==
                  PlayerOwnershipValidationStatus.OwnershipLost)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}.");
    }
}
