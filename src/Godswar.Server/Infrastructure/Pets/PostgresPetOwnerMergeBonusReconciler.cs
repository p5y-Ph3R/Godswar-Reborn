using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

/// <summary>
/// Rebuilds the derived owner-Merge stat rows for active pets before gameplay
/// listeners start. PostgreSQL remains authoritative for Savvy and the
/// published balance; the materialized character bonuses are reconstructable.
/// </summary>
internal static class PostgresPetOwnerMergeBonusReconciler
{
    private const long ReconciliationLockId = 0x5045544D5245434E;

    public static async Task<int> ReconcileAsync(
        string connectionString,
        IPetOwnerMergeContentCatalog content,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "A PostgreSQL connection string is required.",
                nameof(connectionString));
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        return await ReconcileAsync(
            dataSource,
            content,
            cancellationToken);
    }

    public static async Task<int> ReconcileAsync(
        NpgsqlDataSource dataSource,
        IPetOwnerMergeContentCatalog content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(content);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await AcquireLockAsync(connection, transaction, cancellationToken);

        var pets = await ReadActivePetsAsync(
            connection,
            transaction,
            cancellationToken);
        var reconciled = 0;
        foreach (var pet in pets)
        {
            var rawSavvy = await ReadTotalSavvyAsync(
                connection,
                transaction,
                pet.PetId,
                cancellationToken);
            var savvy = PetSoulContractPolicy.ResolveDisplayedTotal(
                rawSavvy,
                pet.SoulContractStage);
            var contribution =
                PetOwnerMergeContributionCalculator.Calculate(
                    savvy,
                    content);
            var effects = PetOwnerMergeContributionCalculator
                .ToEffectValues(contribution);
            if (await HasCurrentBonusProjectionAsync(
                    connection,
                    transaction,
                    pet,
                    content.Revision.Sha256,
                    effects,
                    cancellationToken))
            {
                continue;
            }
            await ReplaceBonusRowsAsync(
                connection,
                transaction,
                pet,
                content.Revision.Sha256,
                contribution,
                cancellationToken);
            reconciled++;
        }

        await transaction.CommitAsync(cancellationToken);
        return reconciled;
    }

    private static async Task AcquireLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@lockId);",
            connection,
            transaction);
        command.Parameters.AddWithValue("lockId", ReconciliationLockId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<ActiveMergePet>>
        ReadActivePetsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT pet.id, pet.revision, pet.soul_contract_stage
            FROM public.character_pets pet
            WHERE pet.contributes_to_character
            ORDER BY pet.id
            FOR UPDATE;
            """,
            connection,
            transaction);
        var pets = new List<ActiveMergePet>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pets.Add(new ActiveMergePet(
                reader.GetInt64(0),
                reader.GetInt64(1),
                checked((byte)reader.GetInt16(2))));
        }
        return pets;
    }

    private static async Task<bool> HasCurrentBonusProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActiveMergePet pet,
        string balanceRevision,
        IReadOnlyList<PetOwnerMergeEffectValue> expected,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT effect_code, effect_value, revision, balance_revision
            FROM public.character_pet_character_bonuses
            WHERE pet_id = @petId
            ORDER BY effect_code
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", pet.PetId);
        var index = 0;
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (index >= expected.Count ||
                reader.GetInt16(0) != (short)expected[index].Effect ||
                reader.GetDecimal(1) != expected[index].Value ||
                reader.GetInt64(2) != pet.PetRevision ||
                reader.IsDBNull(3) ||
                !string.Equals(
                    reader.GetString(3),
                    balanceRevision,
                    StringComparison.Ordinal))
            {
                return false;
            }
            index++;
        }
        return index == expected.Count;
    }

    private static async Task<PetSavvy> ReadTotalSavvyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT stat.stat_code,
                   stat.initial_savvy,
                   stat.added_savvy,
                   stat.base_growth_rate,
                   stat.growth_acceleration,
                   stat.rarity_added_savvy,
                   pet.level,
                   pet.initial_savvy_source_version
            FROM public.character_pet_stat_values stat
            JOIN public.character_pets pet
              ON pet.id = stat.pet_id
            WHERE stat.pet_id = @petId
            ORDER BY stat.stat_code
            FOR UPDATE OF stat;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        var initial = new decimal[6];
        var added = new decimal[6];
        var growth = new decimal[6];
        var acceleration = new decimal[6];
        var rarity = new decimal[6];
        var count = 0;
        var rowsWithRarity = 0;
        var level = 0;
        string? sourceVersion = null;
        var sourceVersionRead = false;
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (count >= initial.Length ||
                reader.GetInt16(0) != count + 1)
            {
                throw new InvalidDataException(
                    $"Active owner-Merge pet {petId} has malformed Savvy rows.");
            }
            initial[count] = reader.GetDecimal(1);
            added[count] = reader.GetDecimal(2);
            growth[count] = reader.GetDecimal(3);
            acceleration[count] = reader.GetDecimal(4);
            if (!reader.IsDBNull(5))
            {
                rarity[count] = reader.GetDecimal(5);
                rowsWithRarity++;
            }
            var rowLevel = reader.GetInt16(6);
            if (level != 0 && rowLevel != level)
            {
                throw new InvalidDataException(
                    $"Active owner-Merge pet {petId} changed level while loading Savvy.");
            }
            level = rowLevel;
            var rowSourceVersion = reader.IsDBNull(7)
                ? null
                : reader.GetString(7);
            if (sourceVersionRead && !string.Equals(
                    sourceVersion,
                    rowSourceVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Active owner-Merge pet {petId} changed Savvy provenance while loading.");
            }
            sourceVersion = rowSourceVersion;
            sourceVersionRead = true;
            count++;
        }
        if (count != initial.Length)
        {
            throw new InvalidDataException(
                $"Active owner-Merge pet {petId} is missing Savvy rows.");
        }
        if (rowsWithRarity != 0 && rowsWithRarity != initial.Length)
        {
            throw new InvalidDataException(
                $"Active owner-Merge pet {petId} has partial scaled-Added provenance.");
        }

        var materializedAdded = ToPetSavvy(added);
        var baseGrowth = ToPetSavvy(growth);
        var growthAcceleration = ToPetSavvy(acceleration);
        if (rowsWithRarity == initial.Length &&
            (!string.Equals(
                sourceVersion,
                PetSavvyRuntimeSemantics.SourceVersion,
                StringComparison.Ordinal) ||
             materializedAdded !=
                PetSavvyRuntimeSemantics.ResolveLevelScaledAdded(
                    level,
                    baseGrowth,
                    growthAcceleration)))
        {
            throw new InvalidDataException(
                $"Active owner-Merge pet {petId} has stale scaled Added values.");
        }

        return PetSavvyRuntimeSemantics.ResolvePlayerVisibleTotal(
            level,
            ToPetSavvy(initial),
            materializedAdded,
            baseGrowth,
            growthAcceleration,
            rowsWithRarity == 0 ? null : ToPetSavvy(rarity));
    }

    private static PetSavvy ToPetSavvy(IReadOnlyList<decimal> values) =>
        new(
            values[0],
            values[1],
            values[2],
            values[3],
            values[4],
            values[5]);

    private static async Task ReplaceBonusRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActiveMergePet pet,
        string balanceRevision,
        PetOwnerStatContribution contribution,
        CancellationToken cancellationToken)
    {
        await using (var delete = new NpgsqlCommand(
                         """
                         DELETE FROM public.character_pet_character_bonuses
                         WHERE pet_id = @petId;
                         """,
                         connection,
                         transaction))
        {
            delete.Parameters.AddWithValue("petId", pet.PetId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var effects = PetOwnerMergeContributionCalculator
            .ToEffectValues(contribution);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO public.character_pet_character_bonuses (
                pet_id, effect_code, effect_value, revision,
                balance_revision
            )
            SELECT @petId, value.effect_code, value.effect_value,
                   @petRevision, @balanceRevision
            FROM unnest(
                @effectCodes::smallint[],
                @effectValues::numeric[]
            ) AS value(effect_code, effect_value);
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("petId", pet.PetId);
        insert.Parameters.AddWithValue("petRevision", pet.PetRevision);
        insert.Parameters.AddWithValue(
            "balanceRevision",
            balanceRevision);
        insert.Parameters.Add(
            "effectCodes",
            NpgsqlDbType.Array | NpgsqlDbType.Smallint).Value =
            effects.Select(static value => (short)value.Effect).ToArray();
        insert.Parameters.Add(
            "effectValues",
            NpgsqlDbType.Array | NpgsqlDbType.Numeric).Value =
            effects.Select(static value => value.Value).ToArray();
        if (await insert.ExecuteNonQueryAsync(cancellationToken) !=
            effects.Count)
        {
            throw new InvalidDataException(
                $"Owner-Merge bonuses for pet {pet.PetId} were not rebuilt exactly.");
        }
    }

    private readonly record struct ActiveMergePet(
        long PetId,
        long PetRevision,
        byte SoulContractStage);
}
