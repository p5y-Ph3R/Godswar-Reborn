using Godswar.Server.Game;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<ForgeTransactionResult> ForgeEquipmentAsync(
        int accountId,
        int characterId,
        ForgeTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int silver;
        await using (var command = new NpgsqlCommand("""
            SELECT "Money"
            FROM character_base
            WHERE account_id = @accountId AND id = @characterId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            if (scalar is null || scalar is DBNull)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ForgeTransactionResult(
                    ForgeTransactionStatus.CharacterNotFound,
                    null,
                    0,
                    0,
                    0,
                    CompactItemEntry.Empty,
                    CompactItemEntry.Empty,
                    "Character was not found.");
            }

            silver = Convert.ToInt32(scalar);
        }

        // Lock and read the uncapped source rows. The compatibility loadout view
        // intentionally clamps client-visible quality/grade and must never be
        // used as the source of a persistent forge mutation.
        var (_, kitBag) = await LoadAuthoritativeItemProjectionsForUpdateAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        var equipmentBefore = request is not null &&
                              request.Equipment.KitBagSlot is >= 0 and < KitBagProjectionSlots
            ? KitBagSlots.GetItem(kitBag, request.Equipment.KitBagSlot)
            : CompactItemEntry.Empty;

        if (!ForgePersistencePlanner.TryCreate(
                kitBag,
                silver,
                request,
                System.Security.Cryptography.RandomNumberGenerator.GetInt32(100),
                out var plan,
                out var rejectionStatus,
                out var rejectionReason))
        {
            await transaction.CommitAsync(cancellationToken);
            return new ForgeTransactionResult(
                rejectionStatus,
                await GetCharacterByIdAsync(characterId, cancellationToken),
                0,
                0,
                0,
                equipmentBefore,
                equipmentBefore,
                rejectionReason);
        }

        await using (var command = new NpgsqlCommand("""
            UPDATE character_base
            SET "Money" = "Money" - @silverCost
            WHERE account_id = @accountId
              AND id = @characterId
              AND "Money" >= @silverCost
            RETURNING "Money";
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("silverCost", plan!.Calculation.SilverCost);
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            var updatedSilver = await command.ExecuteScalarAsync(cancellationToken);
            if (updatedSilver is null ||
                updatedSilver is DBNull ||
                Convert.ToInt32(updatedSilver) != plan.UpdatedSilver)
            {
                throw new InvalidOperationException(
                    $"Forge wallet for character {characterId} changed after it was locked.");
            }
        }

        foreach (var mutation in plan!.Mutations)
        {
            await ApplyForgeSlotMutationAsync(
                connection,
                transaction,
                characterId,
                mutation,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var refreshedCharacter = await GetCharacterByIdAsync(characterId, cancellationToken);
        return new ForgeTransactionResult(
            plan.Succeeded
                ? ForgeTransactionStatus.Succeeded
                : ForgeTransactionStatus.FailedRoll,
            refreshedCharacter,
            (int)plan.Calculation.Operation,
            plan.Calculation.SuccessProbability,
            plan.Calculation.SilverCost,
            equipmentBefore,
            plan.Succeeded
                ? plan.Calculation.SuccessEquipment
                : plan.Calculation.FailureEquipment);
    }

    public async Task<GearEnhancementTransactionResult> EnhanceGearAsync(
        int accountId,
        int characterId,
        GearEnhancementRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            SELECT id
            FROM character_base
            WHERE account_id = @accountId AND id = @characterId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            if (scalar is null || scalar is DBNull)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new GearEnhancementTransactionResult(null, null);
            }
        }

        // Build the plan only from the uncapped, authoritative item rows while
        // both the character and its inventory are locked in this transaction.
        var (_, kitBag) = await LoadAuthoritativeItemProjectionsForUpdateAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        var enhancement = GearEnhancementPlanner.Create(kitBag, request);
        if (!enhancement.Committed)
        {
            // Rejected plans never write or commit inventory state.
            await transaction.RollbackAsync(cancellationToken);
            return new GearEnhancementTransactionResult(
                enhancement,
                await GetCharacterByIdAsync(characterId, cancellationToken));
        }

        foreach (var mutation in enhancement.Mutations)
        {
            await ApplyGearEnhancementSlotMutationAsync(
                connection,
                transaction,
                characterId,
                mutation,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new GearEnhancementTransactionResult(
            enhancement,
            await GetCharacterByIdAsync(characterId, cancellationToken));
    }

    public async Task<GearMentorTransactionResult> ProcessGearMentorAsync(
        int accountId,
        int characterId,
        GearMentorRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int playerLevel;
        await using (var command = new NpgsqlCommand("""
            SELECT fighter_job_lv
            FROM character_base
            WHERE account_id = @accountId AND id = @characterId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            if (scalar is null || scalar is DBNull)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new GearMentorTransactionResult(null, null);
            }

            playerLevel = Convert.ToInt32(scalar);
        }

        // Lock and plan against the uncapped authoritative rows. Client-visible
        // loadout projections clamp high qualities/grades and are never a safe
        // source for a destructive decomposition transaction.
        var (_, kitBag) = await LoadAuthoritativeItemProjectionsForUpdateAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        var result = GearMentorPlanner.Create(kitBag, playerLevel, request);
        if (!result.Committed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new GearMentorTransactionResult(
                result,
                await GetCharacterByIdAsync(characterId, cancellationToken));
        }

        foreach (var mutation in result.Mutations)
        {
            await ApplyGearMentorSlotMutationAsync(
                connection,
                transaction,
                characterId,
                mutation,
                cancellationToken);
        }

        // Read the refreshed projection on the same connection before commit.
        // A failed read therefore rolls the mutation back instead of allowing a
        // committed recipe to surface to the caller as a failed/stale request.
        var refreshedCharacter = await GetCharacterByIdAsync(
                connection,
                transaction,
                characterId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "Committed Gear Mentor character could not be reloaded inside its transaction.");

        await transaction.CommitAsync(cancellationToken);
        return new GearMentorTransactionResult(result, refreshedCharacter);
    }

    public async Task<GameCharacter?> ApplyWeaponHolyStoneAsync(
        int accountId,
        int characterId,
        HolyStoneOperation operation,
        int targetKitBagSlot,
        int socketIndex,
        int stoneKitBagSlot,
        int destinationKitBagSlot,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        byte profession;
        await using (var command = new NpgsqlCommand("""
            SELECT cb.profession
            FROM character_base cb
            WHERE cb.account_id = @accountId AND cb.id = @characterId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            profession = (byte)reader.GetInt16(0);
        }

        var (equipment, kitBag) = await LoadAuthoritativeItemProjectionsForUpdateAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);

        if (!HolyStonePersistencePlanner.TryCreate(
                equipment,
                kitBag,
                profession,
                operation,
                targetKitBagSlot,
                socketIndex,
                stoneKitBagSlot,
                destinationKitBagSlot,
                out var plan,
                out var summary))
        {
            await transaction.CommitAsync(cancellationToken);
            Console.WriteLine(
                $"[holy-stone] mutation ignored character={characterId} operation={operation} targetSlot={targetKitBagSlot} socket={socketIndex} stoneSlot={stoneKitBagSlot}: {summary}");
            return null;
        }

        foreach (var mutation in plan!.Mutations)
        {
            await ApplyHolyStoneSlotMutationAsync(
                connection,
                transaction,
                characterId,
                mutation,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        Console.WriteLine(
            $"[holy-stone] mutated character={characterId} operation={operation} targetSlot={targetKitBagSlot} socket={socketIndex} stoneSlot={stoneKitBagSlot}: {summary}");

        return await GetCharacterByIdAsync(characterId, cancellationToken);
    }

}
