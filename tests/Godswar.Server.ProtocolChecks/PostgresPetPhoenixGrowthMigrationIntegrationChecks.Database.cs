using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetPhoenixGrowthMigrationIntegrationChecks
{
    private static async Task<IReadOnlyList<Fixture>> InsertFixturesAsync(
        NpgsqlDataSource dataSource,
        string token)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var fixtures = new List<Fixture>();
        try
        {
            await using var parentCommand = new NpgsqlCommand(
                """
                WITH created_account AS (
                    INSERT INTO public.accounts (username)
                    VALUES (@username)
                    RETURNING id
                ),
                created_owner AS (
                    INSERT INTO public.character_base (account_id, name)
                    SELECT id, @ownerName
                    FROM created_account
                    RETURNING id
                )
                INSERT INTO public.character_pets (
                    user_id, species_id, name, sex, level, aptitude,
                    remaining_lifetime, growth_revealed,
                    initial_savvy_baseline_total,
                    initial_savvy_policy_version,
                    rarity_added_savvy_baseline_total,
                    rarity_added_savvy_policy_version,
                    initial_savvy_source_version,
                    revision
                )
                SELECT
                    owner.id, 1, @petPrefix || fixture.kind,
                    0, fixture.level, fixture.aptitude, 600,
                    fixture.growth_revealed,
                    fixture.savvy_total, fixture.savvy_policy,
                    fixture.savvy_total, fixture.savvy_policy,
                    'savvy-plus-growth-v2', fixture.revision
                FROM created_owner owner
                CROSS JOIN (VALUES
                    ('convert', 6::smallint, 2::smallint, false,
                     95, 'project-v3', 7::bigint),
                    ('weak', 1::smallint, 3::smallint, false,
                     30, 'project-v3', 17::bigint),
                    ('revealed', 16::smallint, 120::smallint, true,
                     4975, 'legacy-high-savvy-range-v1', 27::bigint)
                ) fixture(
                    kind, aptitude, level, growth_revealed,
                    savvy_total, savvy_policy, revision)
                RETURNING id, name;
                """,
                connection,
                transaction);
            parentCommand.Parameters.AddWithValue("username", $"m071_{token}");
            parentCommand.Parameters.AddWithValue("ownerName", $"M071{token}");
            parentCommand.Parameters.AddWithValue("petPrefix", $"M071{token}_");
            await using (var reader = await parentCommand.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var name = reader.GetString(1);
                    fixtures.Add(new Fixture(
                        reader.GetInt64(0),
                        name.EndsWith("convert", StringComparison.Ordinal)
                            ? FixtureKind.Convert
                            : name.EndsWith("weak", StringComparison.Ordinal)
                                ? FixtureKind.Weak
                                : FixtureKind.Revealed));
                }
            }

            Check.Equal(3, fixtures.Count, "all Phoenix migration cases are seeded");
            foreach (var fixture in fixtures)
            {
                await InsertStatsAsync(connection, transaction, fixture);
            }
            await transaction.CommitAsync();
            return fixtures;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task InsertStatsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Fixture fixture)
    {
        var savvy = fixture.Kind switch
        {
            FixtureKind.Convert => new[]
            {
                15.84m, 15.84m, 15.83m, 15.83m, 15.83m, 15.83m
            },
            FixtureKind.Weak => Enumerable.Repeat(5m, 6).ToArray(),
            _ => new[]
            {
                663.33m, 862.33m, 729.67m,
                928.67m, 995m, 796m
            }
        };
        var growth = fixture.Kind switch
        {
            FixtureKind.Convert => new[]
            {
                0.38m, 0.41m, 0.40m, 0.47m, 0.44m, 0.45m
            },
            FixtureKind.Weak => new[]
            {
                0.011667m, 0.011667m, 0.011667m,
                0.011667m, 0.011666m, 0.011666m
            },
            _ => new[]
            {
                16.767423m, 15.081458m, 16.762208m,
                13.337792m, 13.332577m, 15.018542m
            }
        };
        var statRevision = fixture.Kind switch
        {
            FixtureKind.Convert => 11L,
            FixtureKind.Weak => 21L,
            _ => 31L
        };

        for (short index = 0; index < 6; index++)
        {
            var delta = fixture.Kind == FixtureKind.Convert && index == 0
                ? 5m
                : fixture.Kind == FixtureKind.Convert && index == 2
                    ? 0.25m
                    : fixture.Kind == FixtureKind.Weak && index == 1
                        ? 2m
                        : 0m;
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO public.character_pet_stat_values (
                    pet_id, stat_code, initial_savvy, added_savvy,
                    base_growth_rate, growth_acceleration,
                    birth_initial_savvy, rarity_added_savvy, revision)
                VALUES (
                    @petId, @statCode, @initialSavvy, @addedSavvy,
                    @baseGrowth, @growthAcceleration,
                    @birthSavvy, @birthSavvy, @revision);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("petId", fixture.PetId);
            command.Parameters.AddWithValue("statCode", checked((short)(index + 1)));
            command.Parameters.AddWithValue(
                "initialSavvy",
                savvy[index] + (fixture.Kind == FixtureKind.Convert && index == 0
                    ? 7m
                    : 0m));
            command.Parameters.AddWithValue("addedSavvy", growth[index] + delta);
            command.Parameters.AddWithValue("baseGrowth", growth[index]);
            command.Parameters.AddWithValue(
                "growthAcceleration",
                fixture.Kind == FixtureKind.Convert && index == 4 ? 0.20m : 0m);
            command.Parameters.AddWithValue("birthSavvy", savvy[index]);
            command.Parameters.AddWithValue("revision", statRevision);
            Check.Equal(
                1,
                await command.ExecuteNonQueryAsync(),
                $"fixture {fixture.Kind} stat {index + 1} is inserted");
        }
    }

    private static async Task<Dictionary<long, PetState>> ReadAllStatesAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<Fixture> fixtures)
    {
        var result = new Dictionary<long, PetState>();
        foreach (var fixture in fixtures)
        {
            result[fixture.PetId] = await ReadStateAsync(dataSource, fixture.PetId);
        }
        return result;
    }

    private static async Task<PetState> ReadStateAsync(
        NpgsqlDataSource dataSource,
        long petId)
    {
        var growthPolicyExpression = await ColumnExistsAsync(
            dataSource,
            "character_pets",
            "growth_activation_policy_version")
            ? "pet.growth_activation_policy_version"
            : "NULL::text";
        var sql = """
            SELECT
                pet.id, pet.user_id, pet.aptitude, pet.level,
                pet.growth_revealed, pet.revision,
                pet.initial_savvy_baseline_total,
                pet.initial_savvy_policy_version,
                pet.rarity_added_savvy_baseline_total,
                pet.rarity_added_savvy_policy_version,
                pet.initial_savvy_source_version,
                @@GROWTH_POLICY@@,
                stat.stat_code, stat.initial_savvy, stat.added_savvy,
                stat.base_growth_rate, stat.growth_acceleration,
                stat.birth_initial_savvy, stat.rarity_added_savvy,
                stat.revision
            FROM public.character_pets pet
            JOIN public.character_pet_stat_values stat ON stat.pet_id = pet.id
            WHERE pet.id = @petId
            ORDER BY stat.stat_code;
            """.Replace(
                "@@GROWTH_POLICY@@",
                growthPolicyExpression,
                StringComparison.Ordinal);
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("petId", petId);
        PetParentState? parent = null;
        var stats = new List<PetStatState>(6);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            parent ??= new PetParentState(
                reader.GetInt64(0), reader.GetInt32(1), reader.GetInt16(2),
                reader.GetInt16(3), reader.GetBoolean(4), reader.GetInt64(5),
                reader.GetInt32(6), reader.GetString(7), reader.GetInt32(8),
                reader.GetString(9), reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11));
            stats.Add(new PetStatState(
                reader.GetInt16(12), reader.GetDecimal(13), reader.GetDecimal(14),
                reader.GetDecimal(15), reader.GetDecimal(16), reader.GetDecimal(17),
                reader.GetDecimal(18), reader.GetInt64(19)));
        }
        return parent is not null && stats.Count == 6
            ? new PetState(parent, stats)
            : throw new InvalidDataException($"Pet {petId} did not return six rows.");
    }

    private static async Task<bool> ColumnExistsAsync(
        NpgsqlDataSource dataSource,
        string table,
        string column)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = @table
                  AND column_name = @column);
            """);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task AssertArchiveAsync(
        NpgsqlDataSource dataSource,
        Fixture fixture,
        PetState before)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT stat_code, old_added_savvy, old_base_growth_rate,
                   old_stat_revision, old_pet_revision, old_growth_revealed
            FROM public.pet_phoenix_growth_activation_archive
            WHERE migration_id = @migrationId AND pet_id = @petId
            ORDER BY stat_code;
            """);
        command.Parameters.AddWithValue("migrationId", MigrationId);
        command.Parameters.AddWithValue("petId", fixture.PetId);
        var index = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var oldStat = before.Stats[index];
            Check.True(
                reader.GetInt16(0) == oldStat.StatCode &&
                reader.GetDecimal(1) == oldStat.AddedSavvy &&
                reader.GetDecimal(2) == oldStat.BaseGrowthRate &&
                reader.GetInt64(3) == oldStat.Revision &&
                reader.GetInt64(4) == before.Parent.Revision &&
                !reader.GetBoolean(5),
                $"archive row {index + 1} is the exact converted before-image");
            index++;
        }
        Check.Equal(6, index, "converted pet has exactly six archive rows");
    }

    private sealed record Fixture(long PetId, FixtureKind Kind);
    private enum FixtureKind { Convert, Weak, Revealed }

    private sealed record PetState(
        PetParentState Parent,
        IReadOnlyList<PetStatState> Stats);

    private sealed record PetParentState(
        long PetId,
        int OwnerId,
        short Aptitude,
        short Level,
        bool GrowthRevealed,
        long Revision,
        int InitialSavvyTotal,
        string InitialSavvyPolicy,
        int RaritySavvyTotal,
        string RaritySavvyPolicy,
        string SavvySource,
        string? GrowthPolicy);

    private sealed record PetStatState(
        short StatCode,
        decimal InitialSavvy,
        decimal AddedSavvy,
        decimal BaseGrowthRate,
        decimal GrowthAcceleration,
        decimal BirthInitialSavvy,
        decimal RarityAddedSavvy,
        long Revision);
}
