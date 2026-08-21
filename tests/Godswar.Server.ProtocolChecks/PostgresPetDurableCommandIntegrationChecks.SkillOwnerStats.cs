using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertLearnedSkillOwnerStatsAsync(
        NpgsqlDataSource dataSource,
        GameplayItemContent itemContent,
        IPetLearnedSkillContentCatalog learned,
        int characterId,
        long petId)
    {
        await ReplaceWithReviewedPassiveSkillsAsync(
            dataSource,
            characterId,
            petId);

        var uncarried = await ReadLearnedSkillOwnerStatsAsync(
            dataSource, itemContent, learned, characterId);
        await SetSkillOwnerSourceAsync(
            dataSource, petId, carried: true, rank: 5.59m,
            skillsActive: true);
        var rankFive = await ReadLearnedSkillOwnerStatsAsync(
            dataSource, itemContent, learned, characterId);
        AssertDelta(
            uncarried, rankFive,
            maxHp: 10_600,
            physicalAttack: 391,
            hit: 108,
            physicalDamageBonus: 690,
            ignorePhysicalDefense: 350,
            "rank 5.59 uses each tier-VI rank-zero step");

        await SetSkillOwnerSourceAsync(
            dataSource, petId, carried: true, rank: 100m,
            skillsActive: true);
        var rankHundred = await ReadLearnedSkillOwnerStatsAsync(
            dataSource, itemContent, learned, characterId);
        AssertDelta(
            uncarried, rankHundred,
            maxHp: 12_500,
            physicalAttack: 461,
            hit: 119,
            physicalDamageBonus: 800,
            ignorePhysicalDefense: 410,
            "rank 100 uses the highest reached tier-VI steps");

        await SetSkillOwnerSourceAsync(
            dataSource, petId, carried: true, rank: 100m,
            skillsActive: false);
        var inactive = await ReadLearnedSkillOwnerStatsAsync(
            dataSource, itemContent, learned, characterId);
        Check.Equal(
            uncarried,
            inactive,
            "inactive skills and an uncarried pet contribute no owner stats");
    }

    private static async Task ReplaceWithReviewedPassiveSkillsAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.character_pets
            SET is_carried = false,
                is_summoned = false,
                contributes_to_character = false,
                rank = 5.59,
                opened_skill_slots = 5,
                available_skill_slots = 5
            WHERE id = @petId
              AND user_id = @characterId;

            DELETE FROM public.character_pet_skills
            WHERE pet_id = @petId;

            INSERT INTO public.character_pet_skills (
                pet_id, skill_id, slot_index, skill_rank,
                skill_experience, is_active, revision
            ) VALUES
                (@petId, 3920, 0, 6, 0, true, 0),
                (@petId, 4519, 1, 6, 0, true, 0),
                (@petId, 4620, 2, 6, 0, true, 0),
                (@petId, 5220, 3, 6, 0, true, 0),
                (@petId, 5620, 4, 6, 0, true, 0);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task SetSkillOwnerSourceAsync(
        NpgsqlDataSource dataSource,
        long petId,
        bool carried,
        decimal rank,
        bool skillsActive)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pets
            SET is_carried = @carried,
                rank = @rank
            WHERE id = @petId;
            UPDATE public.character_pet_skills
            SET is_active = @skillsActive
            WHERE pet_id = @petId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("carried", carried);
        command.Parameters.AddWithValue("rank", rank);
        command.Parameters.AddWithValue("skillsActive", skillsActive);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<LearnedSkillOwnerStats>
        ReadLearnedSkillOwnerStatsAsync(
            NpgsqlDataSource dataSource,
            GameplayItemContent itemContent,
            IPetLearnedSkillContentCatalog learned,
            int characterId)
    {
        var accountId = await ReadOwnerAccountIdAsync(
            dataSource,
            characterId);
        await using var command = dataSource.CreateCommand(
            PostgresCharacterRuntimeItemProjectionSql
                .CalculatedStatsForCharacter);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "itemContentRevision",
            itemContent.Templates.Revision.Sha256);
        command.Parameters.AddWithValue(
            PostgresGameplayContentBinding.ParameterName,
            NpgsqlDbType.Varchar,
            DBNull.Value);
        command.Parameters.AddWithValue(
            PostgresPetLearnedSkillContentBinding.ParameterName,
            learned.Revision.Sha256);
        PostgresHolySpiritBalanceBinding.AddParameters(
            command,
            await PostgresHolySpiritBalanceSnapshotReader.LoadAsync(
                dataSource));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Learned-skill owner-stat projection returned no row.");
        }
        return new(
            reader.GetInt32(reader.GetOrdinal("max_hp")),
            reader.GetInt32(reader.GetOrdinal("physical_attack")),
            reader.GetInt32(reader.GetOrdinal("hit")),
            reader.GetInt32(reader.GetOrdinal("physical_damage_bonus")),
            reader.GetInt32(reader.GetOrdinal("ignore_physical_defense")));
    }

    private static async Task<int> ReadOwnerAccountIdAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT account_id
            FROM public.character_base
            WHERE id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        return await command.ExecuteScalarAsync() is int accountId
            ? accountId
            : throw new InvalidDataException(
                "Learned-skill owner account disappeared.");
    }

    private static void AssertDelta(
        LearnedSkillOwnerStats baseline,
        LearnedSkillOwnerStats actual,
        int maxHp,
        int physicalAttack,
        int hit,
        int physicalDamageBonus,
        int ignorePhysicalDefense,
        string description)
    {
        Check.True(
            actual.MaxHp - baseline.MaxHp == maxHp &&
            actual.PhysicalAttack - baseline.PhysicalAttack ==
                physicalAttack &&
            actual.Hit - baseline.Hit == hit &&
            actual.PhysicalDamageBonus - baseline.PhysicalDamageBonus ==
                physicalDamageBonus &&
            actual.IgnorePhysicalDefense -
                baseline.IgnorePhysicalDefense == ignorePhysicalDefense,
            description);
    }

    private sealed record LearnedSkillOwnerStats(
        int MaxHp,
        int PhysicalAttack,
        int Hit,
        int PhysicalDamageBonus,
        int IgnorePhysicalDefense);
}
