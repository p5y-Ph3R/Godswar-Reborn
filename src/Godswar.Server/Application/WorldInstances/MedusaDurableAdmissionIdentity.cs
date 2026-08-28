using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Application.Realms;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Durable identity of one Medusa admission attempt. It is separate from the
/// target world-instance identity so an exact request can be replayed safely.
/// </summary>
internal readonly record struct MedusaAdmissionId
{
    public MedusaAdmissionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Medusa admission IDs cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public bool IsValid => Value != Guid.Empty;

    public static MedusaAdmissionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Realm-local civil day pinned to the exact calendar revision which resolved
/// it. Calendar revision is evidence, not part of the one-attempt uniqueness
/// key: a calendar publication cannot reset attempts for an already named day.
/// </summary>
internal readonly record struct MedusaRealmDay
{
    public MedusaRealmDay(
        RealmId realmId,
        DateOnly day,
        string calendarTimeZoneId,
        string timeZoneRulesFingerprint,
        long calendarRevision)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        if (day == default)
        {
            throw new ArgumentException(
                "Medusa realm days cannot be default.",
                nameof(day));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            calendarRevision);

        RealmId = realmId;
        Day = day;
        CalendarTimeZoneId = RealmCalendar.ValidateTimeZoneId(
            calendarTimeZoneId);
        MedusaDurableAdmissionPolicy.ValidateHash(
            timeZoneRulesFingerprint,
            nameof(timeZoneRulesFingerprint));
        TimeZoneRulesFingerprint = timeZoneRulesFingerprint;
        CalendarRevision = calendarRevision;
    }

    public RealmId RealmId { get; }

    public DateOnly Day { get; }

    public string? CalendarTimeZoneId { get; }

    /// <summary>
    /// Startup-coordinated hash of the exact timezone rules used to resolve
    /// this civil day. Zone ID and publication revision alone do not pin an
    /// operating-system tzdata version.
    /// </summary>
    public string? TimeZoneRulesFingerprint { get; }

    public long CalendarRevision { get; }

    public bool IsValid =>
        RealmId.IsValid &&
        Day != default &&
        !string.IsNullOrWhiteSpace(CalendarTimeZoneId) &&
        TimeZoneRulesFingerprint is { Length:
            MedusaDurableAdmissionPolicy.Sha256HexLength } &&
        CalendarRevision > 0;
}

/// <summary>
/// Exact open-world NPC endpoint from which an admission was requested.
/// Keeping this separate from the target prevents reconnect code from
/// accidentally treating a dungeon map as new admission authority.
/// </summary>
internal readonly record struct MedusaAdmissionSource
{
    public MedusaAdmissionSource(
        WorldInstanceId worldInstanceId,
        MapId mapId,
        uint npcId)
    {
        if (!worldInstanceId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(worldInstanceId));
        }
        if (!mapId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(mapId));
        }
        ArgumentOutOfRangeException.ThrowIfZero(npcId);

        WorldInstanceId = worldInstanceId;
        MapId = mapId;
        NpcId = npcId;
    }

    public WorldInstanceId WorldInstanceId { get; }

    public MapId MapId { get; }

    public uint NpcId { get; }

    public bool IsValid =>
        WorldInstanceId.IsValid &&
        MapId.IsValid &&
        NpcId > 0;
}
