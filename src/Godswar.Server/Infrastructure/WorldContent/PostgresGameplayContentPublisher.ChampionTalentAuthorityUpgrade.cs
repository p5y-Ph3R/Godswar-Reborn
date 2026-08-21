using System.Globalization;
using Godswar.Server.Application.World;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresGameplayContentPublisher
{
    private const string ChampionAuthorityPublisher =
        "server-database-champion-talent-authority-v1";
    private const string InflatedChampionV3Revision =
        "23D4689494C17F34DDA8C6242520CA2A67169311B478E41DA3172DB3308DF5AA";
    private const decimal ChampionTooltipScale = 2.6m;

    private static readonly ChampionTalentScalar[] ChampionTalentScalars =
    [
        new(50, 3m),
        new(51, 10m),
        new(52, 9m),
        new(53, 50m),
        new(54, 2m),
        new(55, 0.005m),
        new(56, 5m),
        new(57, 16m),
        new(58, 4m),
        new(59, 7m),
        new(60, 3m),
        new(61, 0.01m),
        new(62, 20m),
        new(63, 1.6m),
        new(64, 4m),
        new(65, 1.2m),
        new(66, 7m),
        new(67, 90m),
        new(68, 90m)
    ];

    private static async Task<GameplayContentPublicationResult>
        EnsureChampionTalentAuthorityAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            GameplayContentPublicationResult current,
            CancellationToken cancellationToken)
    {
        var predecessor = await PostgresWorldContentReaderLoader
            .LoadPublishedGameplayContentAsync(
                connection,
                transaction,
                cancellationToken);
        var fingerprint = WorldContentRevisionHasher.HashGameplay(predecessor);
        if (!string.Equals(
                fingerprint.Sha256,
                current.Revision,
                StringComparison.Ordinal) ||
            fingerprint.EntryCount != current.EntryCount)
        {
            throw ChampionUpgradeUnavailable(
                "The current gameplay publication failed its exact hash " +
                "and entry-count signature.");
        }

        var state = ClassifyChampionTalentAuthority(predecessor);
        if (state == ChampionTalentAuthorityState.Corrected)
        {
            AssertCorrectedPublisher(current);
            return current;
        }

        var knownInflatedPublisher =
            string.Equals(current.Publisher, Publisher, StringComparison.Ordinal) ||
            string.Equals(
                current.Publisher,
                V3UpgradePublisher,
                StringComparison.Ordinal);
        if (!string.Equals(current.Source, Source, StringComparison.Ordinal) ||
            !knownInflatedPublisher ||
            !string.Equals(
                current.Revision,
                InflatedChampionV3Revision,
                StringComparison.Ordinal))
        {
            throw ChampionUpgradeUnavailable(
                "The inflated Champion talent publication is not the " +
                "reviewed v3 predecessor.");
        }

        var successor = CreateChampionTalentAuthoritySuccessor(predecessor);
        AssertChampionOnlyDelta(predecessor, successor);
        var revision = WorldContentRevisionHasher.HashGameplay(successor);
        if (await InsertReleaseAsync(
                connection,
                transaction,
                revision,
                successor,
                cancellationToken))
        {
            await CopyChampionAuthoritySuccessorAsync(
                connection,
                transaction,
                current.Revision,
                revision.Sha256,
                successor,
                cancellationToken);
        }

        _ = await PostgresWorldContentReaderLoader.LoadGameplayRevisionAsync(
            connection,
            transaction,
            revision.Sha256,
            successor,
            cancellationToken);
        await RepairMutableChampionTalentAuthorityAsync(
            connection,
            transaction,
            current.Revision,
            cancellationToken);
        await AdvanceChampionAuthorityPublicationAsync(
            connection,
            transaction,
            current.Revision,
            revision.Sha256,
            current.Publisher,
            cancellationToken);
        return new GameplayContentPublicationResult(
            revision.Sha256,
            revision.EntryCount,
            Source,
            Created: true,
            Publisher: ChampionAuthorityPublisher);
    }

    internal static GameplayContentCatalog
        CreateChampionTalentAuthoritySuccessor(
            GameplayContentCatalog predecessor)
    {
        if (ClassifyChampionTalentAuthority(predecessor) !=
            ChampionTalentAuthorityState.Inflated)
        {
            throw ChampionUpgradeUnavailable(
                "The successor requires the exact inflated Champion vector.");
        }

        var values = ChampionTalentScalars.ToDictionary(
            static value => value.Id,
            static value => value.Value);
        var talents = predecessor.Talents
            .Select(talent => values.TryGetValue(talent.Id, out var value) &&
                              talent.ClassId == 1
                ? talent with
                {
                    EffectValue = value,
                    StatsJson = ReplaceEffectPair(
                        talent,
                        value * ChampionTooltipScale,
                        value)
                }
                : talent)
            .ToArray();
        return predecessor with { Talents = talents };
    }

    private static ChampionTalentAuthorityState
        ClassifyChampionTalentAuthority(GameplayContentCatalog content)
    {
        var champion = content.Talents
            .Where(static talent => talent.ClassId == 1)
            .ToDictionary(static talent => talent.Id);
        if (champion.Count != ChampionTalentScalars.Length ||
            ChampionTalentScalars.Any(value => !champion.ContainsKey(value.Id)))
        {
            throw ChampionUpgradeUnavailable(
                "The publication does not contain Champion talents 50-68 " +
                "exactly.");
        }

        ChampionTalentAuthorityState? state = null;
        foreach (var scalar in ChampionTalentScalars)
        {
            var talent = champion[scalar.Id];
            var candidate = talent.EffectValue == scalar.Value
                ? ChampionTalentAuthorityState.Corrected
                : talent.EffectValue == scalar.Value * ChampionTooltipScale
                    ? ChampionTalentAuthorityState.Inflated
                    : throw ChampionUpgradeUnavailable(
                        $"Champion talent {scalar.Id} has an unreviewed scalar.");
            var expected = candidate == ChampionTalentAuthorityState.Corrected
                ? scalar.Value
                : scalar.Value * ChampionTooltipScale;
            AssertSingleEffectPair(talent, expected);
            if (state is not null && state != candidate)
            {
                throw ChampionUpgradeUnavailable(
                    "The Champion talent publication mixes corrected and " +
                    "inflated scalars.");
            }

            state = candidate;
        }

        return state ?? throw ChampionUpgradeUnavailable(
            "The Champion talent publication is empty.");
    }

    private static void AssertCorrectedPublisher(
        GameplayContentPublicationResult current)
    {
        var knownPublisher =
            string.Equals(current.Publisher, Publisher, StringComparison.Ordinal) ||
            string.Equals(
                current.Publisher,
                V3UpgradePublisher,
                StringComparison.Ordinal) ||
            string.Equals(
                current.Publisher,
                ChampionAuthorityPublisher,
                StringComparison.Ordinal);
        if (!string.Equals(current.Source, Source, StringComparison.Ordinal) ||
            !knownPublisher)
        {
            throw ChampionUpgradeUnavailable(
                "The corrected publication has an unknown source or publisher.");
        }
    }

    private static void AssertChampionOnlyDelta(
        GameplayContentCatalog predecessor,
        GameplayContentCatalog successor)
    {
        var changed = predecessor.Talents.Zip(successor.Talents)
            .Where(pair => !ReferenceEquals(pair.First, pair.Second))
            .ToArray();
        if (changed.Length != ChampionTalentScalars.Length ||
            changed.Any(pair =>
                pair.First.Id != pair.Second.Id ||
                pair.First.ClassId != 1 ||
                pair.First with
                {
                    EffectValue = pair.Second.EffectValue,
                    StatsJson = pair.Second.StatsJson
                } != pair.Second))
        {
            throw ChampionUpgradeUnavailable(
                "The successor changes data outside the 19 Champion scalars.");
        }
    }

    private static string ReplaceEffectPair(
        GameplayTalentDefinition talent,
        decimal fromValue,
        decimal toValue)
    {
        var from = $"\"{talent.EffectId},{FormatScalar(fromValue)}\"";
        var to = $"\"{talent.EffectId},{FormatScalar(toValue)}\"";
        var first = talent.StatsJson.IndexOf(from, StringComparison.Ordinal);
        if (first < 0 || first != talent.StatsJson.LastIndexOf(
                from,
                StringComparison.Ordinal))
        {
            throw ChampionUpgradeUnavailable(
                $"Champion talent {talent.Id} has an unexpected raw stat.");
        }

        return talent.StatsJson.Replace(
            from,
            to,
            StringComparison.Ordinal);
    }

    private static void AssertSingleEffectPair(
        GameplayTalentDefinition talent,
        decimal value)
    {
        var token = $"\"{talent.EffectId},{FormatScalar(value)}\"";
        var first = talent.StatsJson.IndexOf(token, StringComparison.Ordinal);
        if (first < 0 || first != talent.StatsJson.LastIndexOf(
                token,
                StringComparison.Ordinal))
        {
            throw ChampionUpgradeUnavailable(
                $"Champion talent {talent.Id} raw stats disagree with its scalar.");
        }
    }

    private static string FormatScalar(decimal value) =>
        value.ToString("G29", CultureInfo.InvariantCulture);

    private static async Task AdvanceChampionAuthorityPublicationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string predecessorRevision,
        string revision,
        string predecessorPublisher,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE gameplay_content_publication
            SET revision = @revision,
                published_at = now(),
                publisher = @publisher
            WHERE family = 'gameplay'
              AND revision = @predecessor_revision
              AND publisher = @predecessor_publisher;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, revision);
        command.Parameters.AddWithValue(
            "publisher",
            NpgsqlDbType.Varchar,
            ChampionAuthorityPublisher);
        command.Parameters.AddWithValue(
            "predecessor_revision",
            NpgsqlDbType.Varchar,
            predecessorRevision);
        command.Parameters.AddWithValue(
            "predecessor_publisher",
            NpgsqlDbType.Varchar,
            predecessorPublisher);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw ChampionUpgradeUnavailable(
                "The gameplay publication pointer changed during the " +
                "Champion authority upgrade.");
        }
    }

    private static WorldContentUnavailableException
        ChampionUpgradeUnavailable(string message) =>
        new(
            "gameplay",
            WorldContentFailureReason.RevisionMismatch,
            message);

    private readonly record struct ChampionTalentScalar(int Id, decimal Value);

    private enum ChampionTalentAuthorityState
    {
        Corrected,
        Inflated
    }
}
