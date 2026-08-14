using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetContentPublicationIntegrationChecks
{
    private static async Task AssertMagicJadeAppearanceGroupsAsync(
        NpgsqlDataSource dataSource,
        string revision)
    {
        const string sql =
            """
            SELECT count(*) AS appearance_count,
                   count(DISTINCT magic_jade_item_id) AS item_count,
                   min(magic_jade_item_id) AS minimum_item_id,
                   max(magic_jade_item_id) AS maximum_item_id,
                   count(*) FILTER (WHERE merge_cap = 2.40) AS cap_240,
                   count(*) FILTER (WHERE merge_cap = 4.20) AS cap_420,
                   count(*) FILTER (WHERE merge_cap = 7.80) AS cap_780,
                   count(*) FILTER (
                       WHERE five_spirit_minimum = merge_cap / 2 AND
                             five_spirit_maximum = merge_cap)
                       AS five_spirit_ranges
            FROM public.pet_content_magic_jade_appearance_groups
            WHERE revision = @revision;
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("revision", revision);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(),
            "Magic Jade group query returns its aggregate row");
        Check.True(
            reader.GetInt64(0) == 45 &&
            reader.GetInt64(1) == 45 &&
            reader.GetInt32(2) == 11050 &&
            reader.GetInt32(3) == 11094 &&
            reader.GetInt64(4) == 4 &&
            reader.GetInt64(5) == 2 &&
            reader.GetInt64(6) == 39 &&
            reader.GetInt64(7) == 45,
            "published database exposes all 45 Magic Jades in 4/2/39 cap groups");

        await reader.CloseAsync();
        await using var names = dataSource.CreateCommand(
            """
            SELECT magic_jade_item_id, appearance_name
            FROM public.current_pet_magic_jade_appearance_groups
            ORDER BY magic_jade_item_id;
            """);
        await using var nameReader = await names.ExecuteReaderAsync();
        var expectedNames = PetContentArchitectureChecks
            .ExpectedMagicJadeAppearanceNames;
        for (var index = 0; index < expectedNames.Count; index++)
        {
            Check.True(await nameReader.ReadAsync(),
                "current Magic Jade view has every expected row");
            Check.True(
                nameReader.GetInt32(0) == 11050 + index &&
                nameReader.GetString(1) == expectedNames[index],
                $"Magic Jade {11050 + index} has its canonical appearance");
        }
        Check.True(!await nameReader.ReadAsync(),
            "current Magic Jade view has exactly 45 rows");
    }
}
