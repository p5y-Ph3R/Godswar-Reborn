using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMedusaTitleAwardStoreChecks
{
    private static async Task AssertOwnershipIntegrityFailsClosedAsync(
        NpgsqlDataSource dataSource,
        PostgresMedusaDurableAdmissionStore admissionStore,
        PostgresMedusaTitleAwardStore titleStore)
    {
        var membershipFixture = await CreateConsumedAsync(
            admissionStore,
            MedusaEncounterDifficulty.Enhanced,
            At(8),
            (140, 1401));
        var membershipRequest = Completion(
            membershipFixture,
            TimeSpan.FromMinutes(10));
        CheckStatus(
            MedusaTitleSettlementStatus.Applied,
            (await titleStore.SettleCompletionAsync(membershipRequest)).Status,
            "membership-integrity fixture settles a title");
        await AssertPostgresStateAsync(
            "23503",
            async () =>
            {
                await using var command = dataSource.CreateCommand(
                    """
                    INSERT INTO
                        medusa_admission_foundation.character_title_ownership (
                            character_id, title_key, source_admission_id,
                            source_completion_operation_id, acquired_at)
                    VALUES (
                        @characterId, @titleKey, @admissionId,
                        @operationId, @acquiredAt);
                    """);
                command.Parameters.AddWithValue("characterId", 1499);
                command.Parameters.AddWithValue(
                    "titleKey",
                    MedusaTitleAwardPolicy.ExecutionersKey);
                command.Parameters.AddWithValue(
                    "admissionId",
                    membershipRequest.AdmissionId.Value);
                command.Parameters.AddWithValue(
                    "operationId",
                    membershipRequest.OperationId);
                command.Parameters.Add(
                    "acquiredAt",
                    NpgsqlDbType.TimestampTz).Value =
                    membershipRequest.CompletedAtUtc.UtcDateTime;
                await command.ExecuteNonQueryAsync();
            },
            "outsider ownership cannot reference a settlement roster");
        Check.Equal(
            0,
            (await titleStore.FindOwnershipAsync(1499)).Count,
            "failed outsider insert leaves no ownership");

        var revisionFixture = await CreateConsumedAsync(
            admissionStore,
            MedusaEncounterDifficulty.Enhanced,
            At(9),
            (150, 1501));
        var revisionRequest = Completion(
            revisionFixture,
            TimeSpan.FromMinutes(10));
        Check.True(
            (await titleStore.SettleCompletionAsync(revisionRequest)).IsSuccess,
            "revision-corruption fixture settles a title");
        await ExecuteExactlyOneAsync(
            dataSource,
            """
            UPDATE medusa_admission_foundation.admissions
            SET revision = 6
            WHERE admission_id = @admissionId
              AND state = 5
              AND revision = 5;
            """,
            "admissionId",
            revisionRequest.AdmissionId.Value,
            "SQL-valid Completed revision corruption is installed");
        await AssertThrowsAsync<InvalidDataException>(
            async () => await titleStore.FindOwnershipAsync(1501),
            "ownership read rejects Completed state with revision 6");

        var worldFixture = await CreateConsumedAsync(
            admissionStore,
            MedusaEncounterDifficulty.Enhanced,
            At(10),
            (160, 1601));
        var worldRequest = Completion(
            worldFixture,
            TimeSpan.FromMinutes(10));
        Check.True(
            (await titleStore.SettleCompletionAsync(worldRequest)).IsSuccess,
            "world-corruption fixture settles a title");
        await ExecuteExactlyOneAsync(
            dataSource,
            """
            UPDATE medusa_admission_foundation.medusa_completion_settlements
            SET world_instance_id = @worldInstanceId
            WHERE admission_id = @admissionId;
            """,
            "worldInstanceId",
            WorldInstanceId.New().Value,
            "settlement world corruption is installed",
            ("admissionId", worldRequest.AdmissionId.Value));
        await AssertThrowsAsync<InvalidDataException>(
            async () => await titleStore.FindOwnershipAsync(1601),
            "ownership read rejects mismatched settlement world identity");

        var elapsedFixture = await CreateConsumedAsync(
            admissionStore,
            MedusaEncounterDifficulty.Enhanced,
            At(11),
            (170, 1701));
        var elapsedRequest = Completion(
            elapsedFixture,
            TimeSpan.FromMinutes(10));
        Check.True(
            (await titleStore.SettleCompletionAsync(elapsedRequest)).IsSuccess,
            "elapsed-corruption fixture settles a title");
        await ExecuteExactlyOneAsync(
            dataSource,
            """
            UPDATE medusa_admission_foundation.medusa_completion_settlements
            SET elapsed_microseconds = 540000000
            WHERE admission_id = @admissionId
              AND title_key = 'medusa.challengers';
            """,
            "admissionId",
            elapsedRequest.AdmissionId.Value,
            "SQL-valid settlement elapsed corruption is installed");
        await AssertThrowsAsync<InvalidDataException>(
            async () => await titleStore.FindOwnershipAsync(1701),
            "ownership read rejects elapsed time inconsistent with admission start");
    }

    private static async Task ExecuteExactlyOneAsync(
        NpgsqlDataSource dataSource,
        string sql,
        string parameterName,
        object parameterValue,
        string description,
        params (string Name, object Value)[] additionalParameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(parameterName, parameterValue);
        foreach (var parameter in additionalParameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            description);
    }

    private static async Task AssertPostgresStateAsync(
        string expectedSqlState,
        Func<Task> action,
        string description)
    {
        try
        {
            await action();
        }
        catch (PostgresException exception) when (string.Equals(
            exception.SqlState,
            expectedSqlState,
            StringComparison.Ordinal))
        {
            return;
        }
        catch (PostgresException exception)
        {
            throw new InvalidOperationException(
                $"Assertion failed: {description}; expected PostgreSQL " +
                $"{expectedSqlState}, actual {exception.SqlState}.",
                exception);
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected PostgreSQL " +
            $"{expectedSqlState}.");
    }
}
