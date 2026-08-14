using System.Data;
using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetLearnedSkillContentBootstrapper
{
    private const long PublicationLockId = 0x50534B494C4C5631;
    private const int MaximumSerializableAttempts = 3;

    public static async Task<PinnedPetLearnedSkillContentCatalog> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await LoadOnceAsync(dataSource, cancellationToken);
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.SerializationFailure &&
                attempt < MaximumSerializableAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(25 * attempt),
                    cancellationToken);
            }
        }
    }

    private static async Task<PinnedPetLearnedSkillContentCatalog>
        LoadOnceAsync(
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using (var lockCommand = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(@lockId);",
                         connection,
                         transaction))
        {
            lockCommand.Parameters.AddWithValue("lockId", PublicationLockId);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var baseline = PetLearnedSkillContentBaseline.Create();
        var publishedRevision = await ReadPublishedRevisionAsync(
            connection,
            transaction,
            cancellationToken);
        if (publishedRevision is null)
        {
            await InsertBaselineAsync(
                connection,
                transaction,
                baseline,
                cancellationToken);
            publishedRevision = baseline.Revision.Sha256;
        }
        else if (!publishedRevision.Equals(
                     baseline.Revision.Sha256,
                     StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Published learned pet-skill content is not the reviewed installed-client revision.");
        }

        var result = await ReadRevisionAsync(
            connection,
            transaction,
            publishedRevision,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task InsertBaselineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetLearnedSkillContentCatalog baseline,
        CancellationToken cancellationToken)
    {
        await using (var revision = new NpgsqlCommand(
                         """
                         INSERT INTO public.pet_skill_content_revisions (
                             revision, curve_count, step_count,
                             source, source_sha256
                         ) VALUES (
                             @revision, @curveCount, @stepCount,
                             @source, @sourceSha256
                         )
                         ON CONFLICT (revision) DO NOTHING;
                         """,
                         connection,
                         transaction))
        {
            revision.Parameters.AddWithValue(
                "revision", baseline.Revision.Sha256);
            revision.Parameters.AddWithValue(
                "curveCount", baseline.Revision.CurveCount);
            revision.Parameters.AddWithValue(
                "stepCount", baseline.Revision.StepCount);
            revision.Parameters.AddWithValue(
                "source", baseline.Revision.Source);
            revision.Parameters.AddWithValue(
                "sourceSha256", baseline.Revision.SourceSha256);
            var inserted = await revision.ExecuteNonQueryAsync(
                cancellationToken);
            if (inserted == 0)
            {
                throw new InvalidDataException(
                    "An unpublished learned pet-skill revision already exists.");
            }
        }

        await InsertCurvesAsync(
            connection,
            transaction,
            baseline,
            cancellationToken);
        await InsertStepsAsync(
            connection,
            transaction,
            baseline,
            cancellationToken);
        await using (var seal = new NpgsqlCommand(
                         """
                         UPDATE public.pet_skill_content_revisions
                         SET sealed_at = transaction_timestamp()
                         WHERE revision = @revision
                           AND sealed_at IS NULL;
                         """,
                         connection,
                         transaction))
        {
            seal.Parameters.AddWithValue(
                "revision", baseline.Revision.Sha256);
            if (await seal.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "Learned pet-skill revision was not sealed exactly once.");
            }
        }
        await using var publish = new NpgsqlCommand(
            """
            INSERT INTO public.pet_skill_content_publication (
                singleton, revision
            ) VALUES (true, @revision);
            """,
            connection,
            transaction);
        publish.Parameters.AddWithValue(
            "revision", baseline.Revision.Sha256);
        if (await publish.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "Learned pet-skill publication pointer was not inserted.");
        }
    }

    private static async Task InsertCurvesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetLearnedSkillContentCatalog baseline,
        CancellationToken cancellationToken)
    {
        var curves = baseline.Curves.ToArray();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.pet_skill_curve_definitions (
                revision, family_type, priority, genre, effect,
                opaque_add, opaque_flag,
                required_agility, required_strength, required_accuracy,
                required_technique, required_wisdom, required_luck,
                first_runtime_skill_id
            )
            SELECT @revision, value.*
            FROM unnest(
                @familyTypes::integer[], @priorities::smallint[],
                @genres::integer[], @effects::integer[],
                @adds::integer[], @flags::integer[],
                @agility::numeric[], @strength::numeric[],
                @accuracy::numeric[], @technique::numeric[],
                @wisdom::numeric[], @luck::numeric[],
                @firstIds::integer[]
            ) AS value(
                family_type, priority, genre, effect,
                opaque_add, opaque_flag,
                required_agility, required_strength, required_accuracy,
                required_technique, required_wisdom, required_luck,
                first_runtime_skill_id
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", baseline.Revision.Sha256);
        command.Parameters.AddWithValue("familyTypes",
            curves.Select(static x => x.FamilyType).ToArray());
        command.Parameters.AddWithValue("priorities",
            curves.Select(static x => x.Priority).ToArray());
        command.Parameters.AddWithValue("genres",
            curves.Select(static x => x.Genre).ToArray());
        command.Parameters.AddWithValue("effects",
            curves.Select(static x => x.Effect).ToArray());
        command.Parameters.AddWithValue("adds",
            curves.Select(static x => x.OpaqueAdd).ToArray());
        command.Parameters.AddWithValue("flags",
            curves.Select(static x => x.OpaqueFlag).ToArray());
        command.Parameters.AddWithValue("agility", curves.Select(
            static x => x.LearnTraitRequirement.Agility).ToArray());
        command.Parameters.AddWithValue("strength", curves.Select(
            static x => x.LearnTraitRequirement.Strength).ToArray());
        command.Parameters.AddWithValue("accuracy", curves.Select(
            static x => x.LearnTraitRequirement.Accuracy).ToArray());
        command.Parameters.AddWithValue("technique", curves.Select(
            static x => x.LearnTraitRequirement.Technique).ToArray());
        command.Parameters.AddWithValue("wisdom", curves.Select(
            static x => x.LearnTraitRequirement.Wisdom).ToArray());
        command.Parameters.AddWithValue("luck", curves.Select(
            static x => x.LearnTraitRequirement.Luck).ToArray());
        command.Parameters.AddWithValue("firstIds",
            curves.Select(static x => x.FirstRuntimeSkillId).ToArray());
        if (await command.ExecuteNonQueryAsync(cancellationToken) !=
            curves.Length)
        {
            throw new InvalidDataException(
                "Learned pet-skill curve publication was incomplete.");
        }
    }

    private static async Task InsertStepsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetLearnedSkillContentCatalog baseline,
        CancellationToken cancellationToken)
    {
        var rows = baseline.Curves.SelectMany(curve => curve.Steps.Select(
            step => (curve, step))).ToArray();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.pet_skill_curve_steps (
                revision, family_type, priority, step_order,
                runtime_skill_id, minimum_pet_rank, absolute_value
            )
            SELECT @revision, value.*
            FROM unnest(
                @familyTypes::integer[], @priorities::smallint[],
                @orders::smallint[], @runtimeIds::integer[],
                @minimumRanks::smallint[], @values::numeric[]
            ) AS value(
                family_type, priority, step_order,
                runtime_skill_id, minimum_pet_rank, absolute_value
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", baseline.Revision.Sha256);
        command.Parameters.AddWithValue("familyTypes",
            rows.Select(static x => x.curve.FamilyType).ToArray());
        command.Parameters.AddWithValue("priorities",
            rows.Select(static x => x.curve.Priority).ToArray());
        command.Parameters.AddWithValue("orders",
            rows.Select(static x => x.step.StepOrder).ToArray());
        command.Parameters.AddWithValue("runtimeIds",
            rows.Select(static x => x.step.RuntimeSkillId).ToArray());
        command.Parameters.AddWithValue("minimumRanks",
            rows.Select(static x => x.step.MinimumPetRank).ToArray());
        command.Parameters.AddWithValue("values",
            rows.Select(static x => x.step.AbsoluteValue).ToArray());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != rows.Length)
        {
            throw new InvalidDataException(
                "Learned pet-skill step publication was incomplete.");
        }
    }
}
