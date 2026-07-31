using System.Collections.Immutable;
using System.Data;
using Godswar.Server.Application.Characters;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterSnapshotReader
{
    public Task<CharacterCalculatedStatsSnapshot?>
        ReadCalculatedStatsAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default)
    {
        ValidateRuntimeProjectionIdentity(accountId, characterId);
        return ExecuteRuntimeProjectionAsync(
            token => ReadCalculatedStatsCoreAsync(
                accountId,
                characterId,
                token),
            cancellationToken);
    }

    public Task<bool> IsSkillLearnedAsync(
        int accountId,
        int characterId,
        int skillId,
        CancellationToken cancellationToken = default)
    {
        ValidateRuntimeProjectionIdentity(accountId, characterId);
        ArgumentOutOfRangeException.ThrowIfNegative(skillId);
        return ExecuteRuntimeProjectionAsync(
            token => ReadSkillLearnedCoreAsync(
                accountId,
                characterId,
                skillId,
                token),
            cancellationToken);
    }

    public Task<ImmutableArray<CharacterPetSnapshot>> ReadOwnedPetsAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        ValidateRuntimeProjectionIdentity(accountId, characterId);
        return ExecuteRuntimeProjectionAsync(
            token => ReadOwnedPetsCoreAsync(
                accountId,
                characterId,
                token),
            cancellationToken);
    }

    private async Task<CharacterCalculatedStatsSnapshot?>
        ReadCalculatedStatsCoreAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            CalculatedStatsQuery);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await ReadOptionalCalculatedStatsAsync(
            reader,
            cancellationToken);
    }

    private async Task<bool> ReadSkillLearnedCoreAsync(
        int accountId,
        int characterId,
        int skillId,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            SkillLearnedQuery);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("skillId", skillId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is bool learned
            ? learned
            : throw new InvalidDataException(
                "Skill-learning projection did not return a scalar.");
    }

    private async Task<ImmutableArray<CharacterPetSnapshot>>
        ReadOwnedPetsCoreAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);
        await SetReadOnlyAsync(
            connection,
            transaction,
            cancellationToken);
        ImmutableArray<CharacterPetSnapshot> pets;
        await using (var command = new NpgsqlCommand(
                         PetsQuery,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            pets = await ReadOwnedPetSnapshotsAsync(
                reader,
                accountId,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return pets;
    }

    private static void ValidateRuntimeProjectionIdentity(
        int accountId,
        int characterId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
    }

    private static async Task<T> ExecuteRuntimeProjectionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CharacterSnapshotUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is InvalidDataException or
                InvalidCastException or
                OverflowException or
                IndexOutOfRangeException)
        {
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.InvalidData,
                "PostgreSQL returned an invalid runtime projection.",
                ex);
        }
        catch (Exception ex) when (
            ex is NpgsqlException or
                TimeoutException or
                IOException)
        {
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.ProviderUnavailable,
                "PostgreSQL runtime projection loading is unavailable.",
                ex);
        }
    }

    private const string SkillLearnedQuery =
        """
        SELECT EXISTS (
            SELECT 1
            FROM character_base character
            JOIN character_skills skill
              ON skill.user_id = character.id
            JOIN skill_templates template
              ON template.skill_id = skill.skill_id
            WHERE character.account_id = @accountId
              AND character.id = @characterId
              AND character.lifecycle_state = 'active'
              AND skill.skill_id = @skillId
              AND character.profession = ANY(template.class_ids)
        );
        """;
}
