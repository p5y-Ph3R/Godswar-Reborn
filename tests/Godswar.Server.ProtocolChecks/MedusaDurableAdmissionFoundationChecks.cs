using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaDurableAdmissionFoundationChecks
{
    public const string CheckName =
        "Medusa durable admission immutable contracts";

    public static Task RunAsync()
    {
        AssertLeaseIsFrozenAndValidated();
        AssertCanonicalHashesBindExactEvidence();
        AssertDifficultyCannotBeInferredFromSharedMap();
        AssertStateMachineIsStrictAndTerminal();
        AssertDisposableDatabaseGuardIsBounded();
        return Task.CompletedTask;
    }

    private static void AssertLeaseIsFrozenAndValidated()
    {
        var issuedAt = Utc(10);
        var source = new List<PartyAdmissionMember>
        {
            Member(10, 100, 1),
            Member(20, 200, 2)
        };
        var lease = Lease(source, issuedAt);
        source.Clear();

        Check.Equal(
            2,
            lease.Members.Length,
            "party lease owns a frozen roster copy");
        Check.Equal(
            100,
            lease.Members[0].CharacterId,
            "party lease preserves authoritative order");
        Check.True(
            lease.IsValidAt(issuedAt) &&
            !lease.IsValidAt(issuedAt.AddMinutes(5)),
            "party lease validity is half-open");

        Check.Throws<ArgumentException>(
            () => Lease(
                [Member(10, 100, 1), Member(10, 200, 2)],
                issuedAt),
            "party lease rejects duplicate accounts");
        Check.Throws<ArgumentException>(
            () => Lease(
                [Member(10, 100, 1), Member(20, 100, 2)],
                issuedAt),
            "party lease rejects duplicate characters");
        Check.Throws<ArgumentException>(
            () => new PartyAdmissionLease(
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                99,
                999,
                [Member(10, 100, 1)],
                issuedAt,
                issuedAt.AddMinutes(5)),
            "party lease rejects a leader outside the roster");
        Check.Throws<ArgumentException>(
            () => new PartyAdmissionMember(
                1,
                2,
                default,
                new RealmId(1),
                MedusaIslandPolicy.MinimumLevel,
                new WorldInstanceId(
                    Guid.Parse("30000000-0000-0000-0000-000000000001")),
                new MapId(60)),
            "party lease rejects invalid ownership evidence");
        Check.Throws<ArgumentOutOfRangeException>(
            () => Member(10, 100, 1, level: 89),
            "party lease rejects missing minimum-level evidence");
    }

    private static void AssertCanonicalHashesBindExactEvidence()
    {
        var at = Utc(20);
        var first = Member(10, 100, 1);
        var second = Member(20, 200, 2);
        var lease = Lease([first, second], at);
        var exactCopy = Lease(
            [first, second],
            at,
            lease.LeaseId,
            lease.PartyId);
        var reordered = Lease(
            [second, first],
            at,
            lease.LeaseId,
            lease.PartyId,
            leaderAccountId: first.AccountId,
            leaderCharacterId: first.CharacterId);
        var changedFence = Lease(
            [Member(10, 100, 9), second],
            at,
            lease.LeaseId,
            lease.PartyId);

        Check.Equal(
            MedusaDurableAdmissionPolicy.ComputeRosterHash(lease),
            MedusaDurableAdmissionPolicy.ComputeRosterHash(exactCopy),
            "canonical roster hash is deterministic");
        Check.True(
            MedusaDurableAdmissionPolicy.ComputeRosterHash(lease) !=
                MedusaDurableAdmissionPolicy.ComputeRosterHash(reordered),
            "canonical roster hash binds member order");
        Check.True(
            MedusaDurableAdmissionPolicy.ComputeRosterHash(lease) !=
                MedusaDurableAdmissionPolicy.ComputeRosterHash(changedFence),
            "canonical roster hash binds ownership generation");

        var request = Request(lease, at, MedusaEncounterDifficulty.Normal);
        var replay = new MedusaAdmissionReservationRequest(
            request.AdmissionId,
            request.WorldInstanceId,
            request.RealmDay,
            request.Difficulty,
            request.Source,
            exactCopy,
            request.EncounterContentFingerprint,
            request.RequestedAtUtc);
        Check.Equal(
            request.RequestHash,
            replay.RequestHash,
            "exact reservation replay has one canonical request hash");
        Check.Throws<ArgumentOutOfRangeException>(
            () => Request(
                lease,
                lease.ExpiresAtUtc,
                MedusaEncounterDifficulty.Normal),
            "reservation rejects an expired party lease");
        Check.Throws<ArgumentException>(
            () => Request(
                lease,
                at,
                MedusaEncounterDifficulty.Normal,
                realm: 2),
            "reservation rejects roster eligibility from another realm");
        var mixedSource = Lease(
            [
                Member(10, 100, 1),
                Member(
                    20,
                    200,
                    2,
                    sourceWorldInstanceId:
                        Guid.Parse("30000000-0000-0000-0000-000000000099"))
            ],
            at);
        Check.Throws<ArgumentException>(
            () => Request(
                mixedSource,
                at,
                MedusaEncounterDifficulty.Normal),
            "reservation rejects a mixed-source frozen roster");

        var changedCalendar = new MedusaAdmissionReservationRequest(
            request.AdmissionId,
            request.WorldInstanceId,
            new MedusaRealmDay(
                request.RealmDay.RealmId,
                request.RealmDay.Day,
                "Pacific/Auckland",
                new string('D', MedusaDurableAdmissionPolicy.Sha256HexLength),
                request.RealmDay.CalendarRevision),
            request.Difficulty,
            request.Source,
            request.Party,
            request.EncounterContentFingerprint,
            request.RequestedAtUtc);
        Check.True(
            request.RequestHash != changedCalendar.RequestHash,
            "request hash binds calendar time-zone provenance");
        var changedRules = new MedusaAdmissionReservationRequest(
            request.AdmissionId,
            request.WorldInstanceId,
            new MedusaRealmDay(
                request.RealmDay.RealmId,
                request.RealmDay.Day,
                request.RealmDay.CalendarTimeZoneId!,
                new string('E', MedusaDurableAdmissionPolicy.Sha256HexLength),
                request.RealmDay.CalendarRevision),
            request.Difficulty,
            request.Source,
            request.Party,
            request.EncounterContentFingerprint,
            request.RequestedAtUtc);
        Check.True(
            request.RequestHash != changedRules.RequestHash,
            "request hash binds exact timezone-rule provenance");
    }

    private static void AssertDifficultyCannotBeInferredFromSharedMap()
    {
        var at = Utc(30);
        var lease = Lease([Member(10, 100, 1)], at);
        var enhanced = Request(
            lease,
            at,
            MedusaEncounterDifficulty.Enhanced);
        var mythic = new MedusaAdmissionReservationRequest(
            enhanced.AdmissionId,
            enhanced.WorldInstanceId,
            enhanced.RealmDay,
            MedusaEncounterDifficulty.Mythic,
            enhanced.Source,
            enhanced.Party,
            enhanced.EncounterContentFingerprint,
            enhanced.RequestedAtUtc);

        Check.Equal(
            enhanced.ContentMapId,
            mythic.ContentMapId,
            "Enhanced and Mythic retain the shared map-200 identity");
        Check.True(
            enhanced.RequestHash != mythic.RequestHash,
            "request hash binds explicit difficulty despite a shared map");
    }

    private static void AssertStateMachineIsStrictAndTerminal()
    {
        Check.True(
            MedusaDurableAdmissionPolicy.IsAllowedTransition(
                MedusaAdmissionState.Reserved,
                MedusaAdmissionState.RuntimeReady) &&
            MedusaDurableAdmissionPolicy.IsAllowedTransition(
                MedusaAdmissionState.RuntimeReady,
                MedusaAdmissionState.RosterTransferCommitted) &&
            MedusaDurableAdmissionPolicy.IsAllowedTransition(
                MedusaAdmissionState.RosterTransferCommitted,
                MedusaAdmissionState.ConsumedRunning),
            "admission state machine exposes only ordered progress");
        Check.True(
            MedusaDurableAdmissionPolicy.IsAllowedTransition(
                MedusaAdmissionState.Reserved,
                MedusaAdmissionState.Released) &&
            MedusaDurableAdmissionPolicy.IsAllowedTransition(
                MedusaAdmissionState.RuntimeReady,
                MedusaAdmissionState.Released) &&
            !MedusaDurableAdmissionPolicy.IsAllowedTransition(
                MedusaAdmissionState.RosterTransferCommitted,
                MedusaAdmissionState.Released) &&
            !MedusaDurableAdmissionPolicy.IsAllowedTransition(
                MedusaAdmissionState.ConsumedRunning,
                MedusaAdmissionState.Released) &&
            !MedusaDurableAdmissionPolicy.IsAllowedTransition(
                MedusaAdmissionState.Released,
                MedusaAdmissionState.Reserved),
            "release is pre-consumption only and terminal");
        Check.True(
            MedusaDurableAdmissionPolicy.IsAllowedTransition(
                MedusaAdmissionState.ConsumedRunning,
                MedusaAdmissionState.Completed) &&
            MedusaDurableAdmissionPolicy.IsAllowedTransition(
                MedusaAdmissionState.ConsumedRunning,
                MedusaAdmissionState.Abandoned) &&
            MedusaDurableAdmissionPolicy.IsAllowedTransition(
                MedusaAdmissionState.ConsumedRunning,
                MedusaAdmissionState.TimedOut) &&
            !MedusaDurableAdmissionPolicy.IsAllowedTransition(
                MedusaAdmissionState.Completed,
                MedusaAdmissionState.Released),
            "post-consumption outcomes are typed immutable terminal states");
        Check.Throws<ArgumentException>(
            () => new MedusaAdmissionTransitionRequest(
                Guid.NewGuid(),
                MedusaAdmissionId.New(),
                MedusaAdmissionState.Reserved,
                MedusaAdmissionState.ConsumedRunning,
                Utc(40)),
            "transition contract rejects skipped states");
        Check.Throws<ArgumentException>(
            () => new MedusaAdmissionTransitionRequest(
                Guid.NewGuid(),
                MedusaAdmissionId.New(),
                MedusaAdmissionState.ConsumedRunning,
                MedusaAdmissionState.Released,
                Utc(40)),
            "transition contract rejects release after consumption");
    }

    private static void AssertDisposableDatabaseGuardIsBounded()
    {
        Check.True(
            PostgresMedusaAdmissionSchema.IsDisposableDatabaseName(
                "godswar_medusa_01234567") &&
            PostgresMedusaAdmissionSchema.IsDisposableDatabaseName(
                "godswar_b03_0123456789_smoke_01"),
            "schema guard accepts only bounded test families");
        Check.True(
            !PostgresMedusaAdmissionSchema.IsDisposableDatabaseName("postgres") &&
            !PostgresMedusaAdmissionSchema.IsDisposableDatabaseName("godswar") &&
            !PostgresMedusaAdmissionSchema.IsDisposableDatabaseName(
                "godswar_medusa_../../prod"),
            "schema guard rejects broad and unsafe database names");
    }

    internal static PartyAdmissionMember Member(
        int accountId,
        int characterId,
        long generation,
        int realm = 1,
        int level = MedusaIslandPolicy.MinimumLevel,
        Guid? sourceWorldInstanceId = null,
        short sourceMapId = 60) =>
        new(
            accountId,
            characterId,
            new PlayerOwnershipFence(
                Guid.Parse($"00000000-0000-0000-0000-{generation:D12}"),
                generation),
            new RealmId(realm),
            level,
            new WorldInstanceId(
                sourceWorldInstanceId ??
                Guid.Parse("30000000-0000-0000-0000-000000000001")),
            new MapId(sourceMapId));

    internal static PartyAdmissionLease Lease(
        IEnumerable<PartyAdmissionMember> members,
        DateTimeOffset at,
        Guid? leaseId = null,
        Guid? partyId = null,
        int leaderAccountId = 10,
        int leaderCharacterId = 100) =>
        new(
            leaseId ?? Guid.Parse("10000000-0000-0000-0000-000000000001"),
            partyId ?? Guid.Parse("20000000-0000-0000-0000-000000000001"),
            7,
            leaderAccountId,
            leaderCharacterId,
            members,
            at,
            at.AddMinutes(5));

    internal static MedusaAdmissionReservationRequest Request(
        PartyAdmissionLease party,
        DateTimeOffset at,
        MedusaEncounterDifficulty difficulty,
        MedusaAdmissionId? admissionId = null,
        WorldInstanceId? worldInstanceId = null,
        int realm = 1) =>
        new(
            admissionId ?? MedusaAdmissionId.New(),
            worldInstanceId ?? WorldInstanceId.New(),
            new MedusaRealmDay(
                new RealmId(realm),
                new DateOnly(2026, 8, 23),
                "Asia/Manila",
                new string('C', MedusaDurableAdmissionPolicy.Sha256HexLength),
                4),
            difficulty,
            new MedusaAdmissionSource(
                new WorldInstanceId(
                    Guid.Parse("30000000-0000-0000-0000-000000000001")),
                new MapId(60),
                5199),
            party,
            new string('A', MedusaDurableAdmissionPolicy.Sha256HexLength),
            at);

    internal static DateTimeOffset Utc(int minute) =>
        new(2026, 8, 23, 1, minute, 0, TimeSpan.Zero);
}
