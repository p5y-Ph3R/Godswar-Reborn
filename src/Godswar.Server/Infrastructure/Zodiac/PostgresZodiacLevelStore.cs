using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Zodiac;

/// <summary>
/// PostgreSQL authority for one ownership-fenced Zodiac-level upgrade.
/// The supplied data source owns pooling and lifetime.
/// </summary>
internal sealed class PostgresZodiacLevelStore : IZodiacLevelStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _playerOwnershipGuard;

    public PostgresZodiacLevelStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _playerOwnershipGuard =
            new PostgresPlayerOwnershipGuard(_dataSource);
    }

    public async Task<ZodiacLevelUpgradeStoreResult?> UpgradeAsync(
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default)
    {
        ownership.Validate();
        var subject = new CommandSubject(accountId, characterId);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        var ownershipResult =
            await _playerOwnershipGuard.LockCurrentAsync(
                connection,
                transaction,
                subject,
                ownership,
                cancellationToken);
        if (ownershipResult.Status ==
            PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        ownershipResult.RequireCurrent();

        GameCharacter? character = null;
        await using (var command = new NpgsqlCommand("""
            SELECT fighter_job_lv, zodiac_level, zodiac_energy,
                   zodiac_energy_remainder_x100
            FROM character_base
            WHERE id = @characterId
              AND account_id = @accountId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                character = new GameCharacter
                {
                    Level = reader.GetInt32(0),
                    ZodiacLevel = checked((byte)reader.GetInt16(1)),
                    ZodiacEnergy = reader.GetInt32(2),
                    ZodiacEnergyRemainderX100 = reader.GetInt32(3)
                };
            }
        }

        if (character is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var result = Map(ZodiacLevelUpgrade.Apply(character));
        if (!result.Committed)
        {
            await transaction.RollbackAsync(cancellationToken);
            (await _playerOwnershipGuard.ValidateCurrentAsync(
                subject,
                ownership,
                cancellationToken)).RequireCurrent();
            return result;
        }

        await using (var command = new NpgsqlCommand("""
            UPDATE character_base
            SET zodiac_level = @zodiacLevel,
                zodiac_energy = @zodiacEnergy,
                zodiac_energy_remainder_x100 = @zodiacEnergyRemainderX100
            WHERE id = @characterId
              AND account_id = @accountId;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue(
                "zodiacLevel",
                checked((short)result.CurrentLevel));
            command.Parameters.AddWithValue(
                "zodiacEnergy",
                result.CurrentEnergy);
            command.Parameters.AddWithValue(
                "zodiacEnergyRemainderX100",
                result.CurrentEnergyRemainderX100);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        (await _playerOwnershipGuard.ValidateCurrentAsync(
            subject,
            ownership,
            cancellationToken)).RequireCurrent();
        return result;
    }

    private static ZodiacLevelUpgradeStoreResult Map(
        Godswar.Server.State.ZodiacLevelUpgradeResult result)
    {
        var mapped = new ZodiacLevelUpgradeStoreResult(
            result.Status switch
            {
                Godswar.Server.State.ZodiacLevelUpgradeStatus.Succeeded =>
                    ZodiacLevelUpgradeStoreStatus.Succeeded,
                Godswar.Server.State.ZodiacLevelUpgradeStatus
                    .CharacterLevelTooLow =>
                    ZodiacLevelUpgradeStoreStatus.CharacterLevelTooLow,
                Godswar.Server.State.ZodiacLevelUpgradeStatus
                    .InsufficientEnergy =>
                    ZodiacLevelUpgradeStoreStatus.InsufficientEnergy,
                Godswar.Server.State.ZodiacLevelUpgradeStatus
                    .MaximumLevelReached =>
                    ZodiacLevelUpgradeStoreStatus.MaximumLevelReached,
                _ => throw new InvalidDataException(
                    "Unknown Zodiac-level policy result status.")
            },
            result.PreviousLevel,
            result.CurrentLevel,
            result.RequiredCharacterLevel,
            result.EnergyCost,
            result.CurrentEnergy,
            result.CurrentEnergyRemainderX100);
        mapped.Validate();
        return mapped;
    }
}
