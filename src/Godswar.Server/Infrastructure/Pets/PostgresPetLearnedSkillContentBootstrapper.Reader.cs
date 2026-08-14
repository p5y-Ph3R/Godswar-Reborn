using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetLearnedSkillContentBootstrapper
{
    private static async Task<string?> ReadPublishedRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT revision
            FROM public.pet_skill_content_publication
            WHERE singleton;
            """,
            connection,
            transaction);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<PinnedPetLearnedSkillContentCatalog>
        ReadRevisionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        string source;
        string sourceSha256;
        int curveCount;
        int stepCount;
        await using (var header = new NpgsqlCommand(
                         """
                         SELECT source, source_sha256,
                                curve_count, step_count
                         FROM public.pet_skill_content_revisions
                         WHERE revision = @revision
                           AND sealed_at IS NOT NULL;
                         """,
                         connection,
                         transaction))
        {
            header.Parameters.AddWithValue("revision", revision);
            await using var reader =
                await header.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    "Published learned pet-skill revision is absent or unsealed.");
            }
            source = reader.GetString(0);
            sourceSha256 = reader.GetString(1);
            curveCount = reader.GetInt32(2);
            stepCount = reader.GetInt32(3);
        }

        var builders = new Dictionary<
            (int FamilyType, short Priority),
            CurveBuilder>();
        await using (var curves = new NpgsqlCommand(
                         """
                         SELECT family_type, priority, genre, effect,
                                opaque_add, opaque_flag,
                                required_agility, required_strength,
                                required_accuracy, required_technique,
                                required_wisdom, required_luck,
                                first_runtime_skill_id
                         FROM public.pet_skill_curve_definitions
                         WHERE revision = @revision
                         ORDER BY family_type, priority;
                         """,
                         connection,
                         transaction))
        {
            curves.Parameters.AddWithValue("revision", revision);
            await using var reader =
                await curves.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var builder = new CurveBuilder(
                    reader.GetInt32(0), reader.GetInt16(1),
                    reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetInt32(4), reader.GetInt32(5),
                    new PetSkillTraitRequirement(
                        reader.GetDecimal(6), reader.GetDecimal(7),
                        reader.GetDecimal(8), reader.GetDecimal(9),
                        reader.GetDecimal(10), reader.GetDecimal(11)),
                    reader.GetInt32(12),
                    []);
                if (!builders.TryAdd(
                        (builder.FamilyType, builder.Priority),
                        builder))
                {
                    throw new InvalidDataException(
                        "Published learned pet-skill curves are ambiguous.");
                }
            }
        }

        await using (var steps = new NpgsqlCommand(
                         """
                         SELECT family_type, priority, step_order,
                                runtime_skill_id, minimum_pet_rank,
                                absolute_value
                         FROM public.pet_skill_curve_steps
                         WHERE revision = @revision
                         ORDER BY family_type, priority, step_order;
                         """,
                         connection,
                         transaction))
        {
            steps.Parameters.AddWithValue("revision", revision);
            await using var reader =
                await steps.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = (reader.GetInt32(0), reader.GetInt16(1));
                if (!builders.TryGetValue(key, out var builder))
                {
                    throw new InvalidDataException(
                        "A learned pet-skill step has no curve.");
                }
                builder.Steps.Add(new(
                    reader.GetInt16(2), reader.GetInt32(3),
                    reader.GetInt16(4), reader.GetDecimal(5)));
            }
        }

        var result = PinnedPetLearnedSkillContentCatalog.Create(
            source,
            sourceSha256,
            builders.Values.Select(static builder =>
                new PetLearnedSkillCurveContentDefinition(
                    builder.FamilyType,
                    builder.Priority,
                    builder.Genre,
                    builder.Effect,
                    builder.OpaqueAdd,
                    builder.OpaqueFlag,
                    builder.Requirement,
                    builder.FirstRuntimeSkillId,
                    builder.Steps)).ToArray(),
            revision);
        if (result.Revision.CurveCount != curveCount ||
            result.Revision.StepCount != stepCount)
        {
            throw new InvalidDataException(
                "Published learned pet-skill counts are inconsistent.");
        }
        return result;
    }

    private sealed record CurveBuilder(
        int FamilyType,
        short Priority,
        int Genre,
        int Effect,
        int OpaqueAdd,
        int OpaqueFlag,
        PetSkillTraitRequirement Requirement,
        int FirstRuntimeSkillId,
        List<PetLearnedSkillStepContentDefinition> Steps);
}
