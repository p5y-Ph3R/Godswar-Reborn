using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.Realms;

internal interface IRealmCalendarCatalogReader
{
    Task<RealmCalendarCatalog> ReadAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Startup-pinned civil calendar for one logical realm. Durable timestamps
/// remain UTC; only game-facing days, weeks, reset boundaries, and clock
/// projections use this calendar.
/// </summary>
internal sealed class RealmCalendar
{
    public const int MaximumTimeZoneIdLength = 64;
    public const int MaximumUpdatedByLength = 128;

    private readonly TimeZoneInfo _timeZone;

    public RealmCalendar(
        RealmId realmId,
        string timeZoneId,
        long revision,
        DateTimeOffset updatedAtUtc,
        string updatedBy)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        TimeZoneId = ValidateTimeZoneId(timeZoneId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        if (updatedAtUtc == default || updatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Realm calendar update timestamps must be non-default UTC.",
                nameof(updatedAtUtc));
        }
        UpdatedBy = ValidateUpdatedBy(updatedBy);
        _timeZone = ResolveTimeZoneInfo(TimeZoneId, $"Realm {realmId}");
        TimeZoneRulesFingerprint = ComputeTimeZoneRulesFingerprint(_timeZone);

        RealmId = realmId;
        Revision = revision;
        UpdatedAtUtc = updatedAtUtc;
    }

    public RealmId RealmId { get; }

    public string TimeZoneId { get; }

    public long Revision { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public string UpdatedBy { get; }

    /// <summary>
    /// Fingerprint of the resolved host time-zone rules. Workers with the same
    /// persisted IANA ID but different tzdata must not share daily authority.
    /// </summary>
    public string TimeZoneRulesFingerprint { get; }

    internal static RealmCalendar CreateForTesting(
        RealmId realmId,
        string timeZoneId = "Etc/UTC") =>
        new(
            realmId,
            timeZoneId,
            revision: 1,
            DateTimeOffset.UnixEpoch,
            "test-fixture");

    public DateTimeOffset ToRealmTime(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, _timeZone);

    public DateOnly GetDay(DateTimeOffset instant) =>
        DateOnly.FromDateTime(ToRealmTime(instant).DateTime);

    public TimeSpan GetUtcOffset(DateTimeOffset instant) =>
        ToRealmTime(instant).Offset;

    public DateTimeOffset GetStartOfDay(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var endExclusive = local.AddDays(1);
        while (_timeZone.IsInvalidTime(local) && local < endExclusive)
        {
            local = local.AddMinutes(1);
        }
        if (local >= endExclusive)
        {
            throw new InvalidDataException(
                $"Civil day {day:yyyy-MM-dd} does not exist in realm " +
                $"time zone '{TimeZoneId}'.");
        }

        var offset = _timeZone.IsAmbiguousTime(local)
            ? _timeZone.GetAmbiguousTimeOffsets(local).Max()
            : _timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public DateTimeOffset GetNextDayBoundary(DateTimeOffset instant)
    {
        var day = GetDay(instant);
        for (var dayOffset = 1; dayOffset <= 8; dayOffset++)
        {
            try
            {
                var boundary = GetStartOfDay(day.AddDays(dayOffset));
                if (boundary > instant)
                {
                    return boundary;
                }
            }
            catch (InvalidDataException)
            {
                // A political time-zone transition may skip a civil date.
            }
        }

        throw new InvalidDataException(
            $"No next civil-day boundary could be resolved for realm " +
            $"time zone '{TimeZoneId}'.");
    }

    public static DateOnly GetWeekStart(
        DateOnly day,
        DayOfWeek firstDay = DayOfWeek.Monday)
    {
        if (!Enum.IsDefined(firstDay))
        {
            throw new ArgumentOutOfRangeException(nameof(firstDay));
        }
        var elapsed = ((int)day.DayOfWeek - (int)firstDay + 7) % 7;
        return day.AddDays(-elapsed);
    }

    internal static string ValidateTimeZoneId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var segments = value.Split('/');
        if (value.Length > MaximumTimeZoneIdLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            segments.Length < 2 ||
            segments.Any(static segment => segment.Length == 0) ||
            value.Any(static character => !IsTimeZoneIdCharacter(character)))
        {
            throw new ArgumentException(
                "Realm time-zone IDs must be canonical bounded IANA names.",
                nameof(value));
        }
        return value;
    }

    internal static string ValidateUpdatedBy(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumUpdatedByLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(static character => character is < ' ' or > '~'))
        {
            throw new ArgumentException(
                "Realm calendar authors must be bounded printable ASCII.",
                nameof(value));
        }
        return value;
    }

    internal static TimeZoneInfo ResolveTimeZoneInfo(
        string timeZoneId,
        string owner)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception directException)
            when (directException is TimeZoneNotFoundException or
                InvalidTimeZoneException)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(
                    timeZoneId,
                    out var windowsId) &&
                !string.IsNullOrWhiteSpace(windowsId))
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                }
                catch (Exception fallbackException)
                    when (fallbackException is TimeZoneNotFoundException or
                        InvalidTimeZoneException)
                {
                    throw new InvalidDataException(
                        $"{owner} uses unavailable IANA time zone " +
                        $"'{timeZoneId}'.",
                        fallbackException);
                }
            }

            throw new InvalidDataException(
                $"{owner} uses unavailable IANA time zone '{timeZoneId}'.",
                directException);
        }
    }

    internal static string ComputeTimeZoneRulesFingerprint(
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream,
                   Encoding.UTF8,
                   leaveOpen: true))
        {
            // Public AdjustmentRule fields do not expose every offset
            // semantic on all supported target packs. In particular,
            // NoDaylightTransitions can change the effective UTC offset while
            // every publicly readable rule field remains identical. The
            // platform serialization is the canonical full-fidelity ruleset;
            // accepting a conservative mismatch across hosts is safer than
            // allowing workers to assign different realm days under one
            // coordination revision.
            writer.Write("realm-time-zone-rules-v2");
            writer.Write(timeZone.ToSerializedString());
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer()
            .AsSpan(0, checked((int)stream.Length))));
    }

    private static bool IsTimeZoneIdCharacter(char character) =>
        character is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or
            >= '0' and <= '9' or
            '/' or '.' or '_' or '-' or '+';
}

internal sealed class RealmCalendarCatalog
{
    public const int MaximumEntries = 256;

    public RealmCalendarCatalog(IEnumerable<RealmCalendar> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var ordered = entries
            .OrderBy(static entry => entry.RealmId.Value)
            .ToImmutableArray();
        if (ordered.Length is 0 or > MaximumEntries ||
            ordered.Any(static entry => entry is null) ||
            ordered.Select(static entry => entry.RealmId).Distinct().Count() !=
                ordered.Length)
        {
            throw new InvalidDataException(
                "Realm calendar catalog identities must be non-empty, " +
                "unique, and bounded.");
        }

        Entries = ordered;
        CoordinationRevision = ComputeCoordinationRevision(ordered);
    }

    public ImmutableArray<RealmCalendar> Entries { get; }

    public string CoordinationRevision { get; }

    public RealmCalendar Require(RealmId realmId)
    {
        foreach (var entry in Entries)
        {
            if (entry.RealmId == realmId)
            {
                return entry;
            }
        }
        throw new InvalidDataException(
            $"Realm {realmId} has no persisted calendar authority.");
    }

    private static string ComputeCoordinationRevision(
        IEnumerable<RealmCalendar> entries)
    {
        var canonical = new StringBuilder("realm-calendar-catalog-v1\n");
        foreach (var entry in entries)
        {
            canonical.Append("realm:")
                .Append(entry.RealmId.Value)
                .Append('\n')
                .Append("revision:")
                .Append(entry.Revision)
                .Append('\n')
                .Append("time-zone:")
                .Append(entry.TimeZoneId)
                .Append('\n')
                .Append("time-zone-rules:")
                .Append(entry.TimeZoneRulesFingerprint)
                .Append('\n');
        }
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}

internal sealed record RealmCalendarUpdate(
    RealmId RealmId,
    string TimeZoneId,
    long ExpectedRevision,
    string UpdatedBy)
{
    public void Validate()
    {
        if (!RealmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(RealmId));
        }
        _ = RealmCalendar.ValidateTimeZoneId(TimeZoneId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ExpectedRevision);
        _ = RealmCalendar.ValidateUpdatedBy(UpdatedBy);
        _ = RealmCalendar.ResolveTimeZoneInfo(
            TimeZoneId,
            "Realm calendar update");
    }
}

internal enum RealmCalendarUpdateStatus : byte
{
    Updated = 1,
    Unchanged = 2,
    RevisionConflict = 3,
    RealmMissing = 4
}

internal sealed record RealmCalendarUpdateResult(
    RealmCalendarUpdateStatus Status,
    RealmCalendar? Calendar);

internal interface IRealmCalendarSettingsStore
{
    Task<RealmCalendarUpdateResult> TryUpdateAsync(
        RealmCalendarUpdate update,
        CancellationToken cancellationToken = default);
}
