using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetDurableCommandIntegrationChecks
{
    private const short PlatypusFocusBookSlot = 87;

    private static async Task AssertPlatypusFocusActivationAsync(
        PostgresPetDurableCommandExecutor executor,
        NpgsqlDataSource dataSource,
        CommandSubject subject,
        CommandConnectionCorrelation correlation,
        int characterId,
        long petId)
    {
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var pet = new NpgsqlCommand(
                """
                UPDATE public.character_pets
                SET species_id = 31,
                    updated_at = transaction_timestamp()
                WHERE id = @petId
                  AND user_id = @characterId
                  AND is_carried;
                """,
                connection,
                transaction))
            {
                pet.Parameters.AddWithValue("petId", petId);
                pet.Parameters.AddWithValue("characterId", characterId);
                Check.Equal(1, await pet.ExecuteNonQueryAsync(),
                    "Focus fixture selects one Platypus");
            }
            await using (var skill = new NpgsqlCommand(
                """
                UPDATE public.character_pet_skills
                SET skill_id = 4600,
                    skill_rank = 1,
                    skill_experience = 0,
                    revision = 0
                WHERE pet_id = @petId
                  AND slot_index = 0;
                """,
                connection,
                transaction))
            {
                skill.Parameters.AddWithValue("petId", petId);
                Check.Equal(1, await skill.ExecuteNonQueryAsync(),
                    "Focus fixture pins the Platypus starter family");
            }
            await using (var trait = new NpgsqlCommand(
                """
                UPDATE public.character_pet_stat_values
                SET initial_savvy = 100
                WHERE pet_id = @petId
                  AND stat_code = 3;
                """,
                connection,
                transaction))
            {
                trait.Parameters.AddWithValue("petId", petId);
                Check.Equal(1, await trait.ExecuteNonQueryAsync(),
                    "Focus fixture satisfies the Accuracy-64 threshold");
            }
            _ = await SeedBagItemAsync(
                connection,
                transaction,
                characterId,
                PlatypusFocusBookSlot,
                itemId: 10_531,
                stack: 2);
            await transaction.CommitAsync();
        }

        var result = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                BagItemActivationCommandEnvelope.CreateRawLocal(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    new BagItemActivationCommand(
                        PetCommandOperationIdentity.RawLocalServer(
                            Guid.NewGuid(),
                            correlation.ConnectionId),
                        PlatypusFocusBookSlot))));
        var evidence = result.Receipt?.SkillLearn;
        Check.True(
            result is
            {
                Disposition: PetDurableExecutionDisposition.Committed,
                Receipt.Status: PetDurableReceiptStatus.PetSkillLearned
            } &&
            evidence is
            {
                SpeciesId: 31,
                ItemTemplateId: 10_531,
                FamilyType: 413,
                PreviousPriority: 1,
                LearnedPriority: 2,
                PreviousRuntimeSkillId: 4_600,
                LearnedRuntimeSkillId: 4_604
            } &&
            evidence.TraitRequirement.Accuracy == 64m,
            "Platypus Focus II activates only from its exact family and Accuracy threshold");

        await using var verify = dataSource.CreateCommand(
            """
            SELECT skill.skill_id, skill.skill_rank, item.stack
            FROM public.character_pet_skills skill
            JOIN public.character_items item
              ON item.user_id = @characterId
             AND item.item_location = 1
             AND item.slot_index = @bookSlot
            WHERE skill.pet_id = @petId
              AND skill.slot_index = 0;
            """);
        verify.Parameters.AddWithValue("characterId", characterId);
        verify.Parameters.AddWithValue("bookSlot", PlatypusFocusBookSlot);
        verify.Parameters.AddWithValue("petId", petId);
        await using var reader = await verify.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt32(0) == 4_604 &&
            reader.GetInt16(1) == 2 &&
            reader.GetInt16(2) == 1,
            "Focus activation upgrades one row and consumes one exact book");
    }
}
