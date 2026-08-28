using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMedusaDurableAdmissionStoreChecks
{
    private static async Task AssertCrossBoundaryAndInverseOrderClaimsAsync(
        PostgresMedusaDurableAdmissionStore store)
    {
        var sameDayAt = MedusaDurableAdmissionFoundationChecks.Utc(35);
        var sameDayFirst = Request(
            Lease(sameDayAt, (110, 1100)),
            sameDayAt);
        var revisedDay = RebindDay(
            Lease(sameDayAt, (110, 1100)),
            sameDayAt,
            new DateOnly(2026, 8, 23),
            calendarRevision: 99);
        CheckStatus(
            MedusaAdmissionReceiptStatus.Applied,
            (await store.ReserveAsync(sameDayFirst)).Status,
            "first same-civil-day calendar revision reserves");
        CheckStatus(
            MedusaAdmissionReceiptStatus.MemberAttemptConflict,
            (await store.ReserveAsync(revisedDay)).Status,
            "calendar revision cannot reset an existing civil-day attempt");

        var oldDayAt = MedusaDurableAdmissionFoundationChecks.Utc(40);
        var oldDay = RebindDay(
            Lease(oldDayAt, (120, 1200)),
            oldDayAt,
            new DateOnly(2026, 8, 23),
            calendarRevision: 4);
        var nextDayAt = oldDayAt.AddDays(1);
        var nextDay = RebindDay(
            Lease(nextDayAt, (120, 1200)),
            nextDayAt,
            new DateOnly(2026, 8, 24),
            calendarRevision: 4);
        var crossDayResults = await Task.WhenAll(
            store.ReserveAsync(oldDay),
            store.ReserveAsync(nextDay));
        Check.Equal(
            1,
            crossDayResults.Count(result =>
                result.Status == MedusaAdmissionReceiptStatus.Applied),
            "cross-midnight overlapping admission has one winner");
        Check.Equal(
            1,
            crossDayResults.Count(result =>
                result.Status ==
                    MedusaAdmissionReceiptStatus.MemberActiveAdmissionConflict),
            "active member claim concurrently serializes across midnight");
        var active = await store.FindActiveByMemberAsync(
            new RealmId(1),
            1200);
        Check.True(
            active is not null &&
            (active.AdmissionId == oldDay.AdmissionId ||
             active.AdmissionId == nextDay.AdmissionId),
            "active-member lookup resolves the exact winning instance admission");
        var activeAdmissionId = active?.AdmissionId ??
            throw new InvalidOperationException(
                "Active admission lookup unexpectedly returned null.");

        var recovery = await store.ScanRecoverableAsync(
            new RealmId(1),
            after: null,
            maximumCount: 100);
        Check.True(
            recovery.Admissions.Any(snapshot =>
                snapshot.AdmissionId == activeAdmissionId) &&
            recovery.NextCursor is { IsValid: true },
            "bounded recovery scan discovers unfinished admissions without an NPC retry");

        var inverseAt = MedusaDurableAdmissionFoundationChecks.Utc(45);
        var forward = Request(
            Lease(inverseAt, (130, 1300), (140, 1400)),
            inverseAt);
        var reverse = Request(
            Lease(inverseAt, (140, 1400), (130, 1300)),
            inverseAt);
        var inverseResults = await Task.WhenAll(
            store.ReserveAsync(forward),
            store.ReserveAsync(reverse));
        Check.Equal(
            1,
            inverseResults.Count(result =>
                result.Status == MedusaAdmissionReceiptStatus.Applied),
            "inverse roster order has one atomic winner");
        Check.Equal(
            1,
            inverseResults.Count(result =>
                result.Status ==
                    MedusaAdmissionReceiptStatus.MemberAttemptConflict),
            "canonical claim lock order returns conflict rather than deadlock");
    }

    private static MedusaAdmissionReservationRequest RebindDay(
        PartyAdmissionLease party,
        DateTimeOffset at,
        DateOnly day,
        long calendarRevision)
    {
        var baseline = Request(party, at);
        return new MedusaAdmissionReservationRequest(
            MedusaAdmissionId.New(),
            WorldInstanceId.New(),
            new MedusaRealmDay(
                baseline.RealmDay.RealmId,
                day,
                baseline.RealmDay.CalendarTimeZoneId!,
                baseline.RealmDay.TimeZoneRulesFingerprint!,
                calendarRevision),
            baseline.Difficulty,
            baseline.Source,
            party,
            baseline.EncounterContentFingerprint,
            at);
    }
}
