using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMedusaTitleAwardStoreChecks
{
    private sealed record ConsumedFixture(
        MedusaAdmissionReservationRequest Reservation,
        DateTimeOffset ConsumedAtUtc);

    private static async Task<ConsumedFixture> CreateConsumedAsync(
        PostgresMedusaDurableAdmissionStore store,
        MedusaEncounterDifficulty difficulty,
        DateTimeOffset at,
        params (int AccountId, int CharacterId)[] identities)
        => await CreateConsumedAsync(
            store,
            difficulty,
            at,
            null,
            identities);

    private static async Task<ConsumedFixture> CreateConsumedAsync(
        PostgresMedusaDurableAdmissionStore store,
        MedusaEncounterDifficulty difficulty,
        DateTimeOffset at,
        DateOnly? realmDay,
        params (int AccountId, int CharacterId)[] identities)
    {
        var partyMembers = identities.Select((identity, index) =>
            MedusaDurableAdmissionFoundationChecks.Member(
                identity.AccountId,
                identity.CharacterId,
                generation: checked(identity.CharacterId + index + 1)))
            .ToArray();
        var party = new PartyAdmissionLease(
            Guid.NewGuid(),
            Guid.NewGuid(),
            partyRevision: 1,
            identities[0].AccountId,
            identities[0].CharacterId,
            partyMembers,
            at,
            at.AddMinutes(30));
        var baseline =
            MedusaDurableAdmissionFoundationChecks.Request(
                party,
                at,
                difficulty,
                MedusaAdmissionId.New(),
                WorldInstanceId.New());
        var reservation = realmDay is null
            ? baseline
            : new MedusaAdmissionReservationRequest(
                baseline.AdmissionId,
                baseline.WorldInstanceId,
                new MedusaRealmDay(
                    baseline.RealmDay.RealmId,
                    realmDay.Value,
                    baseline.RealmDay.CalendarTimeZoneId!,
                    baseline.RealmDay.TimeZoneRulesFingerprint!,
                    baseline.RealmDay.CalendarRevision),
                baseline.Difficulty,
                baseline.Source,
                baseline.Party,
                baseline.EncounterContentFingerprint,
                baseline.RequestedAtUtc);
        Check.True(
            (await store.ReserveAsync(reservation)).IsSuccess,
            "title fixture reserves an admission");

        var readyAt = at.AddMinutes(1);
        var barrierAt = at.AddMinutes(2);
        var consumedAt = at.AddMinutes(3);
        Check.True(
            (await store.TransitionAsync(new MedusaAdmissionTransitionRequest(
                Guid.NewGuid(),
                reservation.AdmissionId,
                MedusaAdmissionState.Reserved,
                MedusaAdmissionState.RuntimeReady,
                readyAt))).IsSuccess,
            "title fixture marks runtime ready");
        Check.True(
            (await store.TransitionAsync(new MedusaAdmissionTransitionRequest(
                Guid.NewGuid(),
                reservation.AdmissionId,
                MedusaAdmissionState.RuntimeReady,
                MedusaAdmissionState.RosterTransferCommitted,
                barrierAt,
                new MedusaRosterTransferBarrierEvidence(
                    Guid.NewGuid(),
                    new string(
                        'D',
                        MedusaDurableAdmissionPolicy.Sha256HexLength))))).IsSuccess,
            "title fixture commits the transfer barrier");
        Check.True(
            (await store.TransitionAsync(new MedusaAdmissionTransitionRequest(
                Guid.NewGuid(),
                reservation.AdmissionId,
                MedusaAdmissionState.RosterTransferCommitted,
                MedusaAdmissionState.ConsumedRunning,
                consumedAt))).IsSuccess,
            "title fixture consumes the complete roster");
        return new ConsumedFixture(reservation, consumedAt);
    }

    private static MedusaTitleSettlementRequest Completion(
        ConsumedFixture fixture,
        TimeSpan elapsed) =>
        Completion(
            fixture,
            elapsed,
            fixture.ConsumedAtUtc.Add(elapsed),
            fixture.Reservation.WorldInstanceId,
            fixture.Reservation.Difficulty,
            fixture.Reservation.EncounterContentFingerprint,
            fixture.Reservation.RosterHash,
            Members(fixture.Reservation));

    private static MedusaTitleSettlementRequest Completion(
        ConsumedFixture fixture,
        TimeSpan elapsed,
        DateTimeOffset completedAt,
        WorldInstanceId worldInstanceId,
        MedusaEncounterDifficulty difficulty,
        string encounterFingerprint,
        string rosterHash,
        IReadOnlyCollection<MedusaTitleSettlementMember> members) =>
        new(
            fixture.Reservation.AdmissionId,
            worldInstanceId,
            difficulty,
            encounterFingerprint,
            rosterHash,
            fixture.Reservation.RequestHash,
            members,
            completedAt,
            elapsed,
            MedusaIslandPolicy.VictoryScore);

    private static IReadOnlyList<MedusaTitleSettlementRequest>
        ChangedEvidenceRequests(
            ConsumedFixture fixture,
            MedusaTitleSettlementRequest exact) =>
        [
            Completion(
                fixture,
                exact.Elapsed,
                exact.CompletedAtUtc,
                WorldInstanceId.New(),
                exact.Difficulty,
                exact.EncounterContentFingerprint,
                exact.RosterHash,
                exact.FrozenMembers),
            Completion(
                fixture,
                exact.Elapsed,
                exact.CompletedAtUtc,
                exact.WorldInstanceId,
                MedusaEncounterDifficulty.Mythic,
                exact.EncounterContentFingerprint,
                exact.RosterHash,
                exact.FrozenMembers),
            Completion(
                fixture,
                exact.Elapsed,
                exact.CompletedAtUtc,
                exact.WorldInstanceId,
                exact.Difficulty,
                new string('E', MedusaDurableAdmissionPolicy.Sha256HexLength),
                exact.RosterHash,
                exact.FrozenMembers),
            Completion(
                fixture,
                exact.Elapsed,
                exact.CompletedAtUtc,
                exact.WorldInstanceId,
                exact.Difficulty,
                exact.EncounterContentFingerprint,
                new string('F', MedusaDurableAdmissionPolicy.Sha256HexLength),
                exact.FrozenMembers),
            Completion(
                fixture,
                exact.Elapsed,
                exact.CompletedAtUtc,
                exact.WorldInstanceId,
                exact.Difficulty,
                exact.EncounterContentFingerprint,
                exact.RosterHash,
                exact.FrozenMembers.Select((member, index) => index == 0
                    ? new MedusaTitleSettlementMember(
                        member.AccountId + 1_000,
                        member.CharacterId)
                    : member).ToArray()),
            Completion(
                fixture,
                exact.Elapsed,
                exact.CompletedAtUtc.Add(TimeSpan.FromMicroseconds(1)),
                exact.WorldInstanceId,
                exact.Difficulty,
                exact.EncounterContentFingerprint,
                exact.RosterHash,
                exact.FrozenMembers),
            new MedusaTitleSettlementRequest(
                fixture.Reservation.AdmissionId,
                exact.WorldInstanceId,
                exact.Difficulty,
                exact.EncounterContentFingerprint,
                exact.RosterHash,
                new string('9', MedusaDurableAdmissionPolicy.Sha256HexLength),
                exact.FrozenMembers,
                exact.CompletedAtUtc,
                exact.Elapsed,
                exact.FinalScore)
        ];

    private static MedusaTitleSettlementMember[] Members(
        MedusaAdmissionReservationRequest reservation) =>
        reservation.Party.Members.Select(static member =>
            new MedusaTitleSettlementMember(
                member.AccountId,
                member.CharacterId)).ToArray();

    private static DateTimeOffset At(int index) =>
        new DateTimeOffset(2026, 8, 24, 1, 0, 0, TimeSpan.Zero)
            .AddHours(index);

    private static async Task<long> CountOwnershipRowsAsync(
        NpgsqlDataSource dataSource,
        MedusaAdmissionId admissionId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT COUNT(*)
            FROM medusa_admission_foundation.character_title_ownership
            WHERE source_admission_id = @admissionId;
            """);
        command.Parameters.AddWithValue("admissionId", admissionId.Value);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<string> ReadDatabaseAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        return await command.ExecuteScalarAsync() as string ??
            throw new InvalidDataException(
                "PostgreSQL returned no current database name.");
    }

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string description)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected {typeof(TException).Name}.");
    }

    private static void CheckStatus(
        MedusaTitleSettlementStatus expected,
        MedusaTitleSettlementStatus actual,
        string description) =>
        Check.True(
            expected == actual,
            $"{description}: expected {expected}, actual {actual}");
}
