using System.Data;
using Npgsql;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<IReadOnlyList<PetBootstrapSnapshot>> GetOwnedPetsAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        await using var command = new NpgsqlCommand(
            PetBootstrapQuery,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);

        var pets = new List<PetBootstrapRow>();
        var statValues = new Dictionary<long, List<PetStatValueSnapshot>>();
        var characterBonuses =
            new Dictionary<long, List<PetCharacterBonusSnapshot>>();
        var skills = new Dictionary<long, List<PetSkillSnapshot>>();

        await using (var reader =
                     await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                pets.Add(ReadPetBootstrapRow(reader, accountId));
            }

            await ReadStatValuesAsync(reader, statValues, cancellationToken);
            await ReadCharacterBonusesAsync(
                reader,
                characterBonuses,
                cancellationToken);
            await ReadPetSkillsAsync(reader, skills, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return pets
            .Select(pet => pet.ToSnapshot(
                GetRows(statValues, pet.PetId),
                GetRows(characterBonuses, pet.PetId),
                GetRows(skills, pet.PetId)))
            .ToArray();
    }

    private static async Task ReadStatValuesAsync(
        NpgsqlDataReader reader,
        Dictionary<long, List<PetStatValueSnapshot>> rowsByPet,
        CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "Pet bootstrap query did not return stat values.");
        }

        while (await reader.ReadAsync(cancellationToken))
        {
            AddRow(
                rowsByPet,
                reader.GetInt64(0),
                new PetStatValueSnapshot(
                    reader.GetInt16(1),
                    reader.GetDecimal(2),
                    reader.GetDecimal(3),
                    reader.GetDecimal(4),
                    reader.GetDecimal(5),
                    reader.GetInt64(6),
                    reader.IsDBNull(7)
                        ? null
                        : reader.GetDecimal(7),
                    reader.IsDBNull(8)
                        ? null
                        : reader.GetDecimal(8)));
        }
    }

    private static async Task ReadCharacterBonusesAsync(
        NpgsqlDataReader reader,
        Dictionary<long, List<PetCharacterBonusSnapshot>> rowsByPet,
        CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "Pet bootstrap query did not return character bonuses.");
        }

        while (await reader.ReadAsync(cancellationToken))
        {
            AddRow(
                rowsByPet,
                reader.GetInt64(0),
                new PetCharacterBonusSnapshot(
                    reader.GetInt16(1),
                    reader.GetDecimal(2),
                    reader.GetInt64(3)));
        }
    }

    private static async Task ReadPetSkillsAsync(
        NpgsqlDataReader reader,
        Dictionary<long, List<PetSkillSnapshot>> rowsByPet,
        CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "Pet bootstrap query did not return pet skills.");
        }

        while (await reader.ReadAsync(cancellationToken))
        {
            AddRow(
                rowsByPet,
                reader.GetInt64(0),
                new PetSkillSnapshot(
                    reader.GetInt32(1),
                    reader.GetInt16(2),
                    reader.GetInt16(3),
                    reader.GetInt32(4),
                    reader.GetBoolean(5),
                    reader.GetInt64(6)));
        }
    }

    private static PetBootstrapRow ReadPetBootstrapRow(
        NpgsqlDataReader reader,
        int accountId) =>
        new(
            reader.GetInt64(0),
            accountId,
            reader.GetInt32(1),
            reader.GetInt16(2),
            reader.GetString(3),
            checked((byte)reader.GetInt16(4)),
            reader.GetInt16(5),
            reader.GetInt64(6),
            (PetAptitude)reader.GetInt16(7),
            reader.GetDecimal(8),
            reader.GetInt16(9),
            reader.GetInt16(10),
            reader.GetInt32(11),
            checked((byte)reader.GetInt16(12)),
            reader.GetBoolean(13),
            reader.GetInt32(14),
            reader.GetInt32(15),
            reader.GetInt32(16),
            reader.GetInt32(17),
            reader.GetInt32(18),
            reader.GetInt32(19),
            reader.GetBoolean(20),
            reader.GetBoolean(21),
            reader.GetString(22),
            reader.GetBoolean(23),
            reader.GetBoolean(24),
            reader.GetBoolean(25),
            reader.GetInt64(26),
            ToUtcOffset(reader.GetDateTime(27)),
            ToUtcOffset(reader.GetDateTime(28)),
            reader.GetInt16(29),
            reader.GetInt16(30),
            reader.GetInt16(31),
            reader.IsDBNull(32) ? null : reader.GetString(32));

    private static DateTimeOffset ToUtcOffset(DateTime value) =>
        new(value.ToUniversalTime());

    private static void AddRow<T>(
        Dictionary<long, List<T>> rowsByPet,
        long petId,
        T row)
    {
        if (!rowsByPet.TryGetValue(petId, out var rows))
        {
            rows = [];
            rowsByPet.Add(petId, rows);
        }

        rows.Add(row);
    }

    private static IReadOnlyList<T> GetRows<T>(
        Dictionary<long, List<T>> rowsByPet,
        long petId) =>
        rowsByPet.TryGetValue(petId, out var rows)
            ? rows.ToArray()
            : [];

    private sealed record PetBootstrapRow(
        long PetId,
        int AccountId,
        int OwnerCharacterId,
        short SpeciesId,
        string Name,
        byte Sex,
        short Level,
        long Experience,
        PetAptitude Aptitude,
        decimal Rank,
        short CompletedRebirths,
        short RebirthsRemaining,
        int CompletedPetMerges,
        byte SoulContractStage,
        bool HasOwnerMergeTalent,
        int CurrentEnergy,
        int MaximumEnergy,
        int Amity,
        int Satiety,
        int RemainingLifetime,
        int AvailableStatPoints,
        bool GrowthRevealed,
        bool IsBound,
        string ActivityState,
        bool IsCarried,
        bool IsSummoned,
        bool ContributesToCharacter,
        long Revision,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        short OpenedSkillSlots,
        short AvailableSkillSlots,
        short TalentMask,
        string? InitialSavvySourceVersion)
    {
        public PetBootstrapSnapshot ToSnapshot(
            IReadOnlyList<PetStatValueSnapshot> statValues,
            IReadOnlyList<PetCharacterBonusSnapshot> characterBonuses,
            IReadOnlyList<PetSkillSnapshot> skills) =>
            new(
                PetId,
                AccountId,
                OwnerCharacterId,
                SpeciesId,
                Name,
                Sex,
                Level,
                Experience,
                Aptitude,
                Rank,
                CompletedRebirths,
                RebirthsRemaining,
                CompletedPetMerges,
                SoulContractStage > 0,
                HasOwnerMergeTalent,
                CurrentEnergy,
                MaximumEnergy,
                Amity,
                Satiety,
                RemainingLifetime,
                AvailableStatPoints,
                GrowthRevealed,
                IsBound,
                ActivityState,
                IsCarried,
                IsSummoned,
                ContributesToCharacter,
                Revision,
                CreatedAt,
                UpdatedAt,
                statValues,
                characterBonuses,
                skills,
                OpenedSkillSlots,
                AvailableSkillSlots,
                TalentMask,
                InitialSavvySourceVersion,
                SoulContractStage);
    }

    private const string PetBootstrapQuery =
        """
        SELECT
            cp.id,
            cp.user_id,
            cp.species_id,
            cp.name,
            cp.sex,
            cp.level,
            cp.experience,
            cp.aptitude,
            cp.rank,
            cp.completed_rebirths,
            cp.rebirths_remaining,
            cp.completed_pet_merges,
            cp.soul_contract_stage,
            cp.has_owner_merge_talent,
            cp.current_energy,
            cp.maximum_energy,
            cp.amity,
            cp.satiety,
            cp.remaining_lifetime,
            cp.available_stat_points,
            cp.growth_revealed,
            cp.bound,
            cp.activity_state,
            cp.is_carried,
            cp.is_summoned,
            cp.contributes_to_character,
            cp.revision,
            cp.created_at,
            cp.updated_at,
            cp.opened_skill_slots,
            cp.available_skill_slots,
            cp.talent_mask,
            cp.initial_savvy_source_version
        FROM character_pets cp
        INNER JOIN character_base cb
            ON cb.id = cp.user_id
           AND cb.account_id = @accountId
        WHERE cp.user_id = @characterId
          AND cp.activity_state = 'owned'
        ORDER BY cp.id;

        SELECT
            stat_values.pet_id,
            stat_values.stat_code,
            stat_values.initial_savvy,
            stat_values.added_savvy,
            stat_values.base_growth_rate,
            stat_values.growth_acceleration,
            stat_values.revision,
            stat_values.birth_initial_savvy,
            stat_values.rarity_added_savvy
        FROM character_pet_stat_values stat_values
        INNER JOIN character_pets cp
            ON cp.id = stat_values.pet_id
        INNER JOIN character_base cb
            ON cb.id = cp.user_id
           AND cb.account_id = @accountId
        WHERE cp.user_id = @characterId
          AND cp.activity_state = 'owned'
        ORDER BY stat_values.pet_id, stat_values.stat_code;

        SELECT
            bonuses.pet_id,
            bonuses.effect_code,
            bonuses.effect_value,
            bonuses.revision
        FROM character_pet_character_bonuses bonuses
        INNER JOIN character_pets cp
            ON cp.id = bonuses.pet_id
        INNER JOIN character_base cb
            ON cb.id = cp.user_id
           AND cb.account_id = @accountId
        WHERE cp.user_id = @characterId
          AND cp.activity_state = 'owned'
        ORDER BY bonuses.pet_id, bonuses.effect_code;

        SELECT
            skills.pet_id,
            skills.skill_id,
            skills.slot_index,
            skills.skill_rank,
            skills.skill_experience,
            skills.is_active,
            skills.revision
        FROM character_pet_skills skills
        INNER JOIN character_pets cp
            ON cp.id = skills.pet_id
        INNER JOIN character_base cb
            ON cb.id = cp.user_id
           AND cb.account_id = @accountId
        WHERE cp.user_id = @characterId
          AND cp.activity_state = 'owned'
        ORDER BY skills.pet_id, skills.slot_index, skills.skill_id;
        """;
}
