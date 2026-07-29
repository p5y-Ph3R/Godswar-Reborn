using System.Collections.Immutable;
using Godswar.Server.Application.Characters;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterSnapshotReader
{
    private static async Task<IReadOnlyList<PetRow>> ReadPetRowsAsync(
        NpgsqlDataReader reader,
        int accountId,
        CancellationToken cancellationToken)
    {
        var rows = new List<PetRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            CheckRowLimit(
                rows.Count,
                CharacterSnapshotLimits.OwnedPetCount,
                "owned pets");
            rows.Add(new PetRow(
                reader.GetInt64(0),
                accountId,
                reader.GetInt32(1),
                reader.GetInt16(2),
                reader.GetString(3),
                ToByte(reader.GetInt16(4), "pet sex"),
                reader.GetInt16(5),
                reader.GetInt64(6),
                reader.GetInt16(7),
                reader.GetDecimal(8),
                reader.GetInt16(9),
                reader.GetInt16(10),
                reader.GetInt32(11),
                reader.GetBoolean(12),
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
                ToUtcOffset(reader.GetDateTime(28))));
        }

        return rows;
    }

    private static async Task<Dictionary<
        long,
        List<CharacterPetStatValueSnapshot>>> ReadPetStatValuesAsync(
        NpgsqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var rows =
            new Dictionary<long, List<CharacterPetStatValueSnapshot>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            AddPetRow(
                rows,
                reader.GetInt64(0),
                new CharacterPetStatValueSnapshot(
                    reader.GetInt16(1),
                    reader.GetDecimal(2),
                    reader.GetDecimal(3),
                    reader.GetDecimal(4),
                    reader.GetDecimal(5),
                    reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                    reader.IsDBNull(8) ? null : reader.GetDecimal(8)),
                CharacterSnapshotLimits.PetStatValueCount,
                "pet stat values");
        }

        return rows;
    }

    private static async Task<Dictionary<
        long,
        List<CharacterPetBonusSnapshot>>> ReadPetBonusesAsync(
        NpgsqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<long, List<CharacterPetBonusSnapshot>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            AddPetRow(
                rows,
                reader.GetInt64(0),
                new CharacterPetBonusSnapshot(
                    reader.GetInt16(1),
                    reader.GetDecimal(2),
                    reader.GetInt64(3)),
                CharacterSnapshotLimits.PetCharacterBonusCount,
                "pet character bonuses");
        }

        return rows;
    }

    private static async Task<Dictionary<
        long,
        List<CharacterPetSkillSnapshot>>> ReadPetSkillsAsync(
        NpgsqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<long, List<CharacterPetSkillSnapshot>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            AddPetRow(
                rows,
                reader.GetInt64(0),
                new CharacterPetSkillSnapshot(
                    reader.GetInt32(1),
                    reader.GetInt16(2),
                    reader.GetInt16(3),
                    reader.GetInt32(4),
                    reader.GetBoolean(5),
                    reader.GetInt64(6)),
                CharacterSnapshotLimits.PetSkillCount,
                "pet skills");
        }

        return rows;
    }

    private static void AddPetRow<T>(
        Dictionary<long, List<T>> rowsByPet,
        long petId,
        T row,
        int perPetLimit,
        string family)
    {
        if (!rowsByPet.TryGetValue(petId, out var rows))
        {
            if (rowsByPet.Count >= CharacterSnapshotLimits.OwnedPetCount)
            {
                throw new CharacterSnapshotUnavailableException(
                    CharacterSnapshotFailureReason.BoundsExceeded,
                    $"{family} exceeds the owned-pet identity limit.");
            }

            rows = [];
            rowsByPet.Add(petId, rows);
        }

        CheckRowLimit(rows.Count, perPetLimit, family);
        rows.Add(row);
    }

    private static ImmutableArray<T> GetPetRows<T>(
        IReadOnlyDictionary<long, List<T>> rowsByPet,
        long petId) =>
        rowsByPet.TryGetValue(petId, out var rows)
            ? ImmutableArray.CreateRange(rows)
            : ImmutableArray<T>.Empty;

    private sealed record PetRow(
        long PetId,
        int AccountId,
        int OwnerCharacterId,
        short SpeciesId,
        string Name,
        byte Sex,
        short Level,
        long Experience,
        short Aptitude,
        decimal Rank,
        short CompletedRebirths,
        short RebirthsRemaining,
        int CompletedPetMerges,
        bool HasSoulContract,
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
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc)
    {
        public CharacterPetSnapshot ToSnapshot(
            ImmutableArray<CharacterPetStatValueSnapshot> statValues,
            ImmutableArray<CharacterPetBonusSnapshot> bonuses,
            ImmutableArray<CharacterPetSkillSnapshot> skills) =>
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
                HasSoulContract,
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
                CreatedAtUtc,
                UpdatedAtUtc,
                statValues,
                bonuses,
                skills);
    }

    private const string PetsQuery =
        """
        SELECT
            pet.id,
            pet.user_id,
            pet.species_id,
            pet.name,
            pet.sex,
            pet.level,
            pet.experience,
            pet.aptitude,
            pet.rank,
            pet.completed_rebirths,
            pet.rebirths_remaining,
            pet.completed_pet_merges,
            pet.has_soul_contract,
            pet.has_owner_merge_talent,
            pet.current_energy,
            pet.maximum_energy,
            pet.amity,
            pet.satiety,
            pet.remaining_lifetime,
            pet.available_stat_points,
            pet.growth_revealed,
            pet.bound,
            pet.activity_state,
            pet.is_carried,
            pet.is_summoned,
            pet.contributes_to_character,
            pet.revision,
            pet.created_at,
            pet.updated_at
        FROM character_pets pet
        JOIN character_base character
          ON character.id = pet.user_id
         AND character.account_id = @accountId
        WHERE pet.user_id = @characterId
        ORDER BY pet.id;

        SELECT
            value.pet_id,
            value.stat_code,
            value.initial_savvy,
            value.added_savvy,
            value.base_growth_rate,
            value.growth_acceleration,
            value.revision,
            value.birth_initial_savvy,
            value.rarity_added_savvy
        FROM character_pet_stat_values value
        JOIN character_pets pet
          ON pet.id = value.pet_id
        JOIN character_base character
          ON character.id = pet.user_id
         AND character.account_id = @accountId
        WHERE pet.user_id = @characterId
        ORDER BY value.pet_id, value.stat_code;

        SELECT
            bonus.pet_id,
            bonus.effect_code,
            bonus.effect_value,
            bonus.revision
        FROM character_pet_character_bonuses bonus
        JOIN character_pets pet
          ON pet.id = bonus.pet_id
        JOIN character_base character
          ON character.id = pet.user_id
         AND character.account_id = @accountId
        WHERE pet.user_id = @characterId
        ORDER BY bonus.pet_id, bonus.effect_code;

        SELECT
            skill.pet_id,
            skill.skill_id,
            skill.slot_index,
            skill.skill_rank,
            skill.skill_experience,
            skill.is_active,
            skill.revision
        FROM character_pet_skills skill
        JOIN character_pets pet
          ON pet.id = skill.pet_id
        JOIN character_base character
          ON character.id = pet.user_id
         AND character.account_id = @accountId
        WHERE pet.user_id = @characterId
        ORDER BY skill.pet_id, skill.slot_index, skill.skill_id;
        """;
}
