using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<PetLevelUpgradeResult> UpgradePetLevelAsync(
        int accountId,
        int characterId,
        long petId,
        CancellationToken cancellationToken = default)
    {
        if (petId is <= 0 or > uint.MaxValue)
        {
            return PetLevelUpgradeResult.Rejected(
                PetLevelUpgradeStatus.PetNotFound,
                petId);
        }

        var requestId = Guid.NewGuid();
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        if (!await LockOwnedCharacterAsync(
                connection,
                transaction,
                accountId,
                characterId,
                cancellationToken))
        {
            var rejected = PetLevelUpgradeResult.Rejected(
                PetLevelUpgradeStatus.CharacterNotFound,
                petId);
            await WritePetLevelAuditAsync(
                connection,
                transaction,
                requestId,
                characterId,
                petId,
                rejected,
                userId: null,
                referencedPetId: null,
                before: null,
                after: null,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return rejected;
        }

        await EnsureRawPetMutationAllowedAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        var row = await LockPetLevelRowAsync(
            connection,
            transaction,
            characterId,
            petId,
            cancellationToken);
        if (row is null)
        {
            var rejected = PetLevelUpgradeResult.Rejected(
                PetLevelUpgradeStatus.PetNotFound,
                petId);
            await WritePetLevelAuditAsync(
                connection,
                transaction,
                requestId,
                characterId,
                petId,
                rejected,
                characterId,
                referencedPetId: null,
                before: null,
                after: null,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return rejected;
        }

        var stats = await LockPetLevelStatsAsync(
            connection,
            transaction,
            row,
            cancellationToken);
        var before = CreatePetLevelAuditSnapshot(row, stats);
        var rejection = ValidatePetLevelUpgrade(row);
        if (rejection is not null)
        {
            var rejected = PetLevelUpgradeResult.Rejected(
                rejection.Value,
                petId,
                row.Level,
                row.Experience,
                row.Revision);
            await WritePetLevelAuditAsync(
                connection,
                transaction,
                requestId,
                characterId,
                petId,
                rejected,
                characterId,
                petId,
                before,
                before,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return rejected;
        }

        var experienceSpent =
            PetContent.RequiredExperienceForNextLevel(row.Level);
        var nextLevel = checked((short)(row.Level + 1));
        var nextExperience = row.Experience - experienceSpent;
        var nextRevision = await PersistPetLevelUpgradeAsync(
            connection,
            transaction,
            row,
            nextLevel,
            nextExperience,
            cancellationToken);
        var nextStats = await PersistPetLevelStatGrowthAsync(
            connection,
            transaction,
            row,
            nextLevel,
            cancellationToken);
        var result = new PetLevelUpgradeResult(
            PetLevelUpgradeStatus.Succeeded,
            petId,
            row.Level,
            nextLevel,
            row.Experience,
            nextExperience,
            experienceSpent,
            nextRevision,
            CreatePetLevelBasicSavvy(nextStats));
        var after = row with
        {
            Level = nextLevel,
            Experience = nextExperience,
            Revision = nextRevision
        };
        var afterSnapshot =
            CreatePetLevelAuditSnapshot(after, nextStats);
        await WritePetLevelAuditAsync(
            connection,
            transaction,
            requestId,
            characterId,
            petId,
            result,
            characterId,
            petId,
            before,
            afterSnapshot,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<PetLevelRow?> LockPetLevelRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long petId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                level,
                experience,
                activity_state,
                revision,
                initial_savvy_source_version,
                contributes_to_character
            FROM character_pets
            WHERE id = @petId
              AND user_id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("characterId", characterId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PetLevelRow(
                petId,
                reader.GetInt16(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetBoolean(5))
            : null;
    }

    private PetLevelUpgradeStatus? ValidatePetLevelUpgrade(
        PetLevelRow row)
    {
        if (!string.Equals(
                row.ActivityState,
                "owned",
                StringComparison.Ordinal) ||
            row.ContributesToCharacter)
        {
            return PetLevelUpgradeStatus.PetUnavailable;
        }

        if (row.Level >= PetContent.Settings.MaximumLevel)
        {
            return PetLevelUpgradeStatus.MaximumLevel;
        }

        var required =
            PetContent.RequiredExperienceForNextLevel(row.Level);
        return row.Experience < required
            ? PetLevelUpgradeStatus.InsufficientExperience
            : null;
    }

    private static async Task<long> PersistPetLevelUpgradeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PetLevelRow row,
        short nextLevel,
        long nextExperience,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE character_pets
            SET level = @nextLevel,
                experience = @nextExperience,
                revision = revision + 1,
                updated_at = now()
            WHERE id = @petId
              AND revision = @expectedRevision
            RETURNING revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("nextLevel", nextLevel);
        command.Parameters.AddWithValue("nextExperience", nextExperience);
        command.Parameters.AddWithValue("petId", row.PetId);
        command.Parameters.AddWithValue(
            "expectedRevision",
            row.Revision);
        var revision = await command.ExecuteScalarAsync(cancellationToken);
        return revision is long persistedRevision
            ? persistedRevision
            : throw new DBConcurrencyException(
                $"Pet {row.PetId} changed during its locked level-up.");
    }

    private static async Task WritePetLevelAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        int characterId,
        long petId,
        PetLevelUpgradeResult result,
        int? userId,
        long? referencedPetId,
        PetLevelAuditSnapshot? before,
        PetLevelAuditSnapshot? after,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pet_operation_audit (
                request_id,
                user_id,
                user_id_snapshot,
                pet_id,
                pet_id_snapshot,
                operation,
                outcome,
                before_state,
                after_state,
                reason_code
            )
            VALUES (
                @requestId,
                @userId,
                @userIdSnapshot,
                @petId,
                @petIdSnapshot,
                'level_up',
                @outcome,
                @beforeState,
                @afterState,
                @reasonCode
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("requestId", requestId);
        command.Parameters.Add(
                "userId",
                NpgsqlDbType.Integer)
            .Value = (object?)userId ?? DBNull.Value;
        command.Parameters.AddWithValue(
            "userIdSnapshot",
            characterId);
        command.Parameters.Add(
                "petId",
                NpgsqlDbType.Bigint)
            .Value = (object?)referencedPetId ?? DBNull.Value;
        command.Parameters.AddWithValue("petIdSnapshot", petId);
        command.Parameters.AddWithValue(
            "outcome",
            result.Succeeded ? "committed" : "rejected");
        command.Parameters.Add(
                "beforeState",
                NpgsqlDbType.Jsonb)
            .Value = SerializePetLevelAuditState(before)
                ?? (object)DBNull.Value;
        command.Parameters.Add(
                "afterState",
                NpgsqlDbType.Jsonb)
            .Value = SerializePetLevelAuditState(after)
                ?? (object)DBNull.Value;
        command.Parameters.Add(
                "reasonCode",
                NpgsqlDbType.Varchar)
            .Value = result.Succeeded
                ? DBNull.Value
                : result.Status.ToString();
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? SerializePetLevelAuditState(
        PetLevelAuditSnapshot? snapshot) =>
        snapshot is null
            ? null
            : JsonSerializer.Serialize(snapshot);

    private sealed record PetLevelRow(
        long PetId,
        short Level,
        long Experience,
        string ActivityState,
        long Revision,
        string? InitialSavvySourceVersion,
        bool ContributesToCharacter);

}
