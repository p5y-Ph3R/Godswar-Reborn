using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Godswar.Server.Application.Characters;

/// <summary>
/// Adds bounded, low-cardinality telemetry around the application query.
/// No account, character, session, or provider-supplied value is used as a
/// metric label.
/// </summary>
internal sealed class MeasuredCharacterSnapshotReader :
    ICharacterSnapshotReader
{
    private readonly ICharacterSnapshotReader _inner;
    private readonly string _provider;

    public MeasuredCharacterSnapshotReader(
        ICharacterSnapshotReader inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _provider = CharacterSnapshotMetrics.PostgreSqlProvider;
    }

    public async Task<CharacterAccountSnapshot> ReadAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "unexpected_failure";
        try
        {
            var snapshot = await _inner.ReadAsync(
                accountId,
                cancellationToken);
            outcome = snapshot.Character is null
                ? "empty"
                : "loaded";
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch (CharacterSnapshotUnavailableException ex)
        {
            outcome = CharacterSnapshotMetrics.ReasonCode(ex.Reason);
            throw;
        }
        finally
        {
            CharacterSnapshotMetrics.Record(
                _provider,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }
}

internal static class CharacterSnapshotMetrics
{
    internal const string PostgreSqlProvider = "postgresql";

    public const string MeterName =
        "Godswar.Server.Application.Characters";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Queries =
        Meter.CreateCounter<long>(
            "godswar_character_snapshot_queries_total",
            description:
            "Completed logical character snapshot queries by provider and outcome.");
    private static readonly Histogram<double> QueryDuration =
        Meter.CreateHistogram<double>(
            "godswar_character_snapshot_query_duration_ms",
            unit: "ms",
            description:
            "End-to-end duration of one logical character snapshot query.");

    public static void Record(
        string provider,
        string outcome,
        TimeSpan duration)
    {
        var tags = new TagList
        {
            { "provider", provider },
            { "outcome", outcome }
        };
        Queries.Add(1, tags);
        QueryDuration.Record(
            Math.Max(0, duration.TotalMilliseconds),
            tags);
    }

    internal static string ReasonCode(
        CharacterSnapshotFailureReason reason) =>
        reason switch
        {
            CharacterSnapshotFailureReason.AccountNotFound =>
                "account_not_found",
            CharacterSnapshotFailureReason.AmbiguousCharacterSlot =>
                "ambiguous_slot",
            CharacterSnapshotFailureReason.CharacterNotFound =>
                "character_not_found",
            CharacterSnapshotFailureReason.MissingCalculatedStats =>
                "missing_stats",
            CharacterSnapshotFailureReason.OwnershipMismatch =>
                "ownership_mismatch",
            CharacterSnapshotFailureReason.InvalidData =>
                "invalid_data",
            CharacterSnapshotFailureReason.BoundsExceeded =>
                "bounds_exceeded",
            CharacterSnapshotFailureReason.UnsupportedContractVersion =>
                "unsupported_contract",
            CharacterSnapshotFailureReason.ProviderUnavailable =>
                "provider_unavailable",
            _ => "unknown"
        };
}
