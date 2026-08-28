using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private const int CaptureRarityBasisPointCount = 10_000;
    private const int NativeCaptureAptitudeCount = 11;

    private async Task<short> RollCapturedEggQualityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PetSpeciesContentDefinition species,
        uint eggItemId,
        MedusaEncounterDifficulty difficulty,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT aptitude, weight_basis_points
            FROM public.medusa_pet_capture_rarity_weights
            WHERE difficulty = @difficulty
              AND egg_item_id = @eggItemId
            ORDER BY aptitude
            FOR SHARE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "difficulty",
            checked((short)difficulty));
        command.Parameters.AddWithValue(
            "eggItemId",
            checked((int)eggItemId));

        var weights = new List<CaptureRarityWeight>(
            NativeCaptureAptitudeCount);
        await using (var reader =
                     await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                weights.Add(new(
                    reader.GetInt16(0),
                    reader.GetInt32(1)));
            }
        }

        ValidateCaptureRarityWeights(species, difficulty, weights);
        var roll = _petCaptureRarityRollSource.NextRoll();
        if (roll is < 0 or >= CaptureRarityBasisPointCount)
        {
            throw new InvalidDataException(
                "The pet-capture rarity roll is outside the basis-point range.");
        }

        var cumulative = 0;
        foreach (var weight in weights)
        {
            cumulative = checked(cumulative + weight.BasisPoints);
            if (roll < cumulative)
            {
                return weight.Aptitude;
            }
        }

        throw new InvalidDataException(
            "The Medusa pet-capture rarity weights contain a gap.");
    }

    private void ValidateCaptureRarityWeights(
        PetSpeciesContentDefinition species,
        MedusaEncounterDifficulty difficulty,
        IReadOnlyList<CaptureRarityWeight> weights)
    {
        if (difficulty is not (
                MedusaEncounterDifficulty.Enhanced or
                MedusaEncounterDifficulty.Mythic) ||
            weights.Count != NativeCaptureAptitudeCount ||
            weights.Sum(static weight => weight.BasisPoints) !=
                CaptureRarityBasisPointCount ||
            weights.Select(static weight => weight.Aptitude)
                .Distinct()
                .Count() != weights.Count ||
            weights.Any(weight =>
                weight.BasisPoints <= 0 ||
                !_petContent.TryGetAptitude(weight.Aptitude, out _) ||
                !_petContent.TryGetNativeProfile(
                    species.SpeciesId,
                    weight.Aptitude,
                    out _)))
        {
            throw new InvalidDataException(
                "The database does not contain one complete native " +
                "Medusa pet-capture rarity distribution.");
        }
    }

    private readonly record struct CaptureRarityWeight(
        short Aptitude,
        int BasisPoints);
}
