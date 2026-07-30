using System.Data;
using Npgsql;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<PetPresenceTransitionResult>
        TransitionPetPresenceAsync(
            int accountId,
            int characterId,
            long petId,
            PetPresenceOperation operation,
            CancellationToken cancellationToken = default)
    {
        if (petId <= 0)
        {
            return Rejected(
                PetPresenceTransitionStatus.PetNotFound,
                petId);
        }

        var requestId = Guid.NewGuid();
        var auditOperation = ToPetPresenceAuditOperation(operation);
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
            var rejected = Rejected(
                PetPresenceTransitionStatus.CharacterNotFound,
                petId);
            await WritePetPresenceAuditAsync(
                connection,
                transaction,
                requestId,
                characterId,
                petId,
                auditOperation,
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
        var pets = await LockCharacterPetsAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        var target = pets.SingleOrDefault(pet => pet.PetId == petId);
        if (target is null)
        {
            var rejected = Rejected(
                PetPresenceTransitionStatus.PetNotFound,
                petId);
            await WritePetPresenceAuditAsync(
                connection,
                transaction,
                requestId,
                characterId,
                petId,
                auditOperation,
                rejected,
                characterId,
                referencedPetId: null,
                pets,
                pets,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return rejected;
        }

        if (!string.Equals(
                target.ActivityState,
                "owned",
                StringComparison.Ordinal))
        {
            var rejected = Rejected(
                PetPresenceTransitionStatus.PetUnavailable,
                petId,
                target.IsCarried,
                target.IsSummoned);
            await WritePetPresenceAuditAsync(
                connection,
                transaction,
                requestId,
                characterId,
                petId,
                auditOperation,
                rejected,
                characterId,
                target.PetId,
                pets,
                pets,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return rejected;
        }

        var result = operation switch
        {
            PetPresenceOperation.Take =>
                await TakePetAsync(
                    connection,
                    transaction,
                    characterId,
                    petId,
                    cancellationToken),
            PetPresenceOperation.CallOut when !target.IsCarried =>
                Rejected(
                    PetPresenceTransitionStatus.PetNotTaken,
                    petId,
                    target.IsCarried,
                    target.IsSummoned),
            PetPresenceOperation.CallOut =>
                await CallOutPetAsync(
                    connection,
                    transaction,
                    petId,
                    target.IsSummoned,
                    cancellationToken),
            PetPresenceOperation.Recall when !target.IsCarried =>
                Rejected(
                    PetPresenceTransitionStatus.PetNotTaken,
                    petId,
                    target.IsCarried,
                    target.IsSummoned),
            PetPresenceOperation.Recall =>
                await RecallPetAsync(
                    connection,
                    transaction,
                    petId,
                    target.IsSummoned,
                    cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Unknown pet-presence operation.")
        };

        var after = result.Succeeded
            ? await LockCharacterPetsAsync(
                connection,
                transaction,
                characterId,
                cancellationToken)
            : pets;
        await WritePetPresenceAuditAsync(
            connection,
            transaction,
            requestId,
            characterId,
            petId,
            auditOperation,
            result,
            characterId,
            target.PetId,
            pets,
            after,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<bool> LockOwnedCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id
            FROM character_base
            WHERE id = @characterId
              AND account_id = @accountId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("accountId", accountId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<IReadOnlyList<PetPresenceRow>>
        LockCharacterPetsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                id,
                activity_state,
                is_carried,
                is_summoned,
                contributes_to_character
            FROM character_pets
            WHERE user_id = @characterId
            ORDER BY id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);

        var pets = new List<PetPresenceRow>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pets.Add(new PetPresenceRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }

        return pets;
    }

    private static async Task<PetPresenceTransitionResult> TakePetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long petId,
        CancellationToken cancellationToken)
    {
        // Clear the previous selection first. A single multi-row UPDATE can
        // transiently violate the partial one-carried unique index when
        // PostgreSQL visits the new target before the old target.
        await using (var clearPrevious = new NpgsqlCommand(
            """
            UPDATE character_pets
            SET is_carried = false,
                is_summoned = false,
                contributes_to_character = false,
                revision = revision + 1,
                updated_at = now()
            WHERE user_id = @characterId
              AND id <> @petId
              AND (
                    is_carried
                    OR is_summoned
                    OR contributes_to_character
              );
            """,
            connection,
            transaction))
        {
            clearPrevious.Parameters.AddWithValue(
                "characterId",
                characterId);
            clearPrevious.Parameters.AddWithValue("petId", petId);
            await clearPrevious.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var selectTarget = new NpgsqlCommand(
            """
            UPDATE character_pets
            SET is_carried = true,
                is_summoned = false,
                contributes_to_character = false,
                revision = revision + 1,
                updated_at = now()
            WHERE id = @petId
              AND (
                    NOT is_carried
                    OR is_summoned
                    OR contributes_to_character
              );
            """,
            connection,
            transaction);
        selectTarget.Parameters.AddWithValue("petId", petId);
        await selectTarget.ExecuteNonQueryAsync(cancellationToken);
        return Succeeded(petId, isCarried: true, isSummoned: false);
    }

    private static async Task<PetPresenceTransitionResult> CallOutPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        bool alreadySummoned,
        CancellationToken cancellationToken)
    {
        if (!alreadySummoned)
        {
            await UpdateSummonedStateAsync(
                connection,
                transaction,
                petId,
                isSummoned: true,
                cancellationToken);
        }

        return Succeeded(petId, isCarried: true, isSummoned: true);
    }

    private static async Task<PetPresenceTransitionResult> RecallPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        bool wasSummoned,
        CancellationToken cancellationToken)
    {
        if (wasSummoned)
        {
            await UpdateSummonedStateAsync(
                connection,
                transaction,
                petId,
                isSummoned: false,
                cancellationToken);
        }

        return Succeeded(petId, isCarried: true, isSummoned: false);
    }

    private static async Task UpdateSummonedStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        bool isSummoned,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE character_pets
            SET is_summoned = @isSummoned,
                contributes_to_character =
                    CASE
                        WHEN @isSummoned THEN contributes_to_character
                        ELSE false
                    END,
                revision = revision + 1,
                updated_at = now()
            WHERE id = @petId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("isSummoned", isSummoned);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PetPresenceTransitionResult Succeeded(
        long petId,
        bool isCarried,
        bool isSummoned) =>
        new(
            PetPresenceTransitionStatus.Succeeded,
            petId,
            isCarried,
            isSummoned);

    private static PetPresenceTransitionResult Rejected(
        PetPresenceTransitionStatus status,
        long petId,
        bool isCarried = false,
        bool isSummoned = false) =>
        new(status, petId, isCarried, isSummoned);

    private sealed record PetPresenceRow(
        long PetId,
        string ActivityState,
        bool IsCarried,
        bool IsSummoned,
        bool ContributesToCharacter);
}
