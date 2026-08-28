using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaAdmissionSagaCoordinatorChecks
{
    public const string CheckName =
        "Medusa durable admission saga capabilities";

    public static async Task RunAsync()
    {
        AssertContractBoundariesRejectRawAuthority();
        await AssertHappyPathAndCrashReplayAsync();
        await AssertReleaseWinsAfterHiddenPrepareAsync();
        await AssertExpiredPreBarrierStageReleasesAsync();
        await AssertPostBarrierStageLossReconstructsAsync();
        await AssertRuntimeRejectionDoesNotBurnAsync();
        await AssertLeaseExpiryBeforeBarrierReleasesAsync();
        await AssertIssuedLeaseIsNonRevocableThroughBarrierAsync();
        await AssertNonLeaderCannotReserveAsync();
        await AssertRealmDayIsServerDerivedAndReplayStableAsync();
        await AssertDeterministicTokenSurvivesLostEnsureReceiptAsync();
        await AssertAmbiguousReceiptsReplayExactlyAsync();
        await AssertConflictingReceiptsNeverAdvanceAsync();
        await AssertCleanupResultsMustBeExactAsync();
        await AssertCleanupTombstonesDefeatStaleCreationAsync();
        await AssertReleasedCleanupCrashBoundariesReplayAsync();
        await AssertNeverStartedTimeoutStillCleansAsync();
        await AssertTerminalAndOperationPermitsFailClosedAsync();
    }

    private static async Task AssertIssuedLeaseIsNonRevocableThroughBarrierAsync()
    {
        var fixture = new SagaFixture();
        var result = await fixture.Coordinator.ExecuteAsync(fixture.Command);
        Check.True(
            result.Status == MedusaAdmissionSagaStatus.Running &&
            !fixture.Party.CanPublishConflictingPartyMutation(
                fixture.Transfer.PreparedAtUtc) &&
            fixture.Party.CanPublishConflictingPartyMutation(
                fixture.Lease.ExpiresAtUtc),
            "issued party revision is an unrevocable capability through its expiry");
    }

    private static void AssertContractBoundariesRejectRawAuthority()
    {
        var fixture = new SagaFixture();
        Check.Throws<ArgumentOutOfRangeException>(
            () => new MedusaAdmissionStartCommand(
                default,
                MedusaEncounterDifficulty.Normal,
                fixture.Source,
                SagaFixture.ContentFingerprint,
                10,
                100,
                fixture.Lease.Members[0].Ownership,
                fixture.ReceivedAt),
            "default operation identity cannot enter the saga");

        var request = MedusaDurableAdmissionFoundationChecks.Request(
            fixture.Lease,
            fixture.ReceivedAt,
            MedusaEncounterDifficulty.Normal,
            fixture.AdmissionId,
            fixture.WorldInstanceId);
        var reserved = Reserved(request);
        Check.Throws<ArgumentException>(
            () => new MedusaPendingRuntimeSnapshot(
                reserved.AdmissionId,
                reserved.WorldInstanceId,
                reserved.Difficulty,
                reserved.ContentMapId,
                reserved.RosterHash,
                reserved.RequestHash,
                reserved.EncounterContentFingerprint,
                new MedusaPendingStartToken(Guid.NewGuid()),
                MedusaPendingRuntimeState.PendingStart,
                reserved.ReservedAtUtc,
                reserved.ReservedAtUtc,
                null,
                null),
            "runtime snapshot rejects a fresh nonempty transfer token");
        Check.Throws<ArgumentOutOfRangeException>(
            () => MedusaAdmissionSagaOperationIds.RuntimeTransferToken(
                default,
                request.RequestHash),
            "deterministic token derivation rejects a default admission ID");
        Check.Throws<ArgumentException>(
            () => new MedusaRosterTransferPrepareResult(
                default,
                request.AdmissionId,
                request.WorldInstanceId,
                request.RosterHash,
                [],
                default,
                null,
                null,
                null),
            "default hidden-stage result status is rejected");
    }

    private static async Task AssertHappyPathAndCrashReplayAsync()
    {
        var fixture = new SagaFixture();
        var result = await fixture.Coordinator.ExecuteAsync(fixture.Command);
        Check.True(
            result.Status == MedusaAdmissionSagaStatus.Running,
            "saga reaches Running");
        Check.True(
            result.Admission!.State == MedusaAdmissionState.ConsumedRunning,
            "all daily claims consume before start");
        Check.True(
            result.Admission.ConsumedAtUtc == result.Runtime!.StartedAtUtc,
            "runtime starts at durable consume time");
        Check.True(
            fixture.Events.IndexOf("transition:ConsumedRunning") <
                fixture.Events.IndexOf("start"),
            "consume is durably ordered before runtime start");

        var stableStart = result.Runtime.StartedAtUtc;
        var stableToken = result.Runtime.TransferToken;
        fixture.Runtime.LoseProcess();
        fixture.Transfer.LoseProcess();
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var replay = await fixture.Coordinator.ExecuteAsync(fixture.Command);
        Check.True(
            replay.Status == MedusaAdmissionSagaStatus.Running,
            "lost runtime is rematerialized and restarted exactly");
        Check.True(
            stableStart == replay.Runtime!.StartedAtUtc,
            "downtime does not reset the run start/deadline");
        Check.Equal(
            stableToken,
            replay.Runtime.TransferToken,
            "runtime transfer token survives process loss");
        Check.Equal(
            result.Admission.EncounterContentFingerprint,
            replay.Admission!.EncounterContentFingerprint,
            "pre-start content fingerprint excludes delayed start clocks");
    }

    private static async Task AssertReleaseWinsAfterHiddenPrepareAsync()
    {
        var fixture = new SagaFixture();
        fixture.Store.ReleaseWinsOnNextBarrier = true;
        var result = await fixture.Coordinator.ExecuteAsync(fixture.Command);
        Check.True(
            result.Status ==
                MedusaAdmissionSagaStatus.TransferRejectedCompensated,
            "release winning the barrier race is compensated");
        Check.Equal(0, fixture.Transfer.HiddenCount,
            "stable admission abort removes hidden capacity");
        Check.Equal(0, fixture.Transfer.CommitCalls,
            "released admission cannot publicly commit transfer");
        Check.Equal(1, fixture.Transfer.AbortCalls,
            "hidden cleanup is reconstructible without a stage receipt");
        Check.True(
            fixture.Runtime.Current!.State ==
                MedusaPendingRuntimeState.Released,
            "empty runtime is retired after durable release");
    }

    private static async Task AssertExpiredPreBarrierStageReleasesAsync()
    {
        var fixture = new SagaFixture();
        fixture.Transfer.ExpiresAtUtc = fixture.ReceivedAt.AddMinutes(3);
        var result = await fixture.Coordinator.ExecuteAsync(fixture.Command);
        Check.True(
            result.Status ==
                MedusaAdmissionSagaStatus.TransferRejectedCompensated,
            "stage expired before barrier releases admission");
        Check.Equal(0, fixture.Transfer.CommitCalls,
            "expired pre-barrier stage never commits");
        Check.Equal(0, fixture.Transfer.HiddenCount,
            "expired hidden stage is explicitly aborted");
    }

    private static async Task AssertPostBarrierStageLossReconstructsAsync()
    {
        var fixture = new SagaFixture();
        fixture.Store.AfterTransition = snapshot =>
        {
            if (snapshot.State != MedusaAdmissionState.RosterTransferCommitted)
            {
                return;
            }
            fixture.Transfer.LoseProcess();
            fixture.Clock.Advance(TimeSpan.FromMinutes(20));
        };
        var result = await fixture.Coordinator.ExecuteAsync(fixture.Command);
        Check.True(
            result.Status == MedusaAdmissionSagaStatus.Running,
            "post-barrier stage loss resumes rather than releases");
        Check.Equal(1, fixture.Transfer.ReconstructedCommitCount,
            "barrier evidence reconstructs exact hidden stage after expiry");
        Check.True(
            result.Admission!.State is not (
                MedusaAdmissionState.Released or
                MedusaAdmissionState.ReleasedCleaned),
            "irreversible barrier is never released");
    }

    private static async Task AssertRuntimeRejectionDoesNotBurnAsync()
    {
        var fixture = new SagaFixture();
        fixture.Runtime.EnsureFailure =
            MedusaPendingRuntimeStatus.RejectedNoPublication;
        var result = await fixture.Coordinator.ExecuteAsync(fixture.Command);
        Check.True(
            result.Status ==
                MedusaAdmissionSagaStatus.RuntimeRejectedCompensated,
            "failed exact runtime creation releases reservation");
        Check.True(
            result.Admission!.State == MedusaAdmissionState.ReleasedCleaned,
            "failed creation does not burn the daily attempt");
        Check.Equal(0, fixture.Transfer.CommitCalls,
            "failed runtime creation cannot reach transfer");
    }

    private static async Task AssertNonLeaderCannotReserveAsync()
    {
        var fixture = new SagaFixture();
        var nonLeader = fixture.Lease.Members[1];
        var command = new MedusaAdmissionStartCommand(
            fixture.Command.Operation,
            fixture.Command.Difficulty,
            fixture.Command.Source,
            fixture.Command.EncounterContentFingerprint,
            nonLeader.AccountId,
            nonLeader.CharacterId,
            nonLeader.Ownership,
            fixture.Command.ReceivedAtUtc);
        var result = await fixture.Coordinator.ExecuteAsync(command);
        Check.True(
            result.Status == MedusaAdmissionSagaStatus.PartyRejected,
            "nonleader invocation is rejected");
        Check.Equal(0, fixture.Store.ReserveCalls,
            "nonleader rejection happens before daily reservation");
    }

    private static async Task AssertRealmDayIsServerDerivedAndReplayStableAsync()
    {
        var fixture = new SagaFixture();
        var first = await fixture.Coordinator.ExecuteAsync(fixture.Command);
        var issuedDay = fixture.Party.LastRequest!.RealmDay;
        Check.Equal(
            fixture.Calendar.GetDay(fixture.ReceivedAt),
            issuedDay.Day,
            "realm day derives from server ReceivedAt and pinned calendar");
        Check.True(fixture.Calendar.TimeZoneId == issuedDay.CalendarTimeZoneId,
            "calendar timezone provenance is frozen");
        Check.True(
            fixture.Calendar.TimeZoneRulesFingerprint ==
                issuedDay.TimeZoneRulesFingerprint,
            "resolved timezone-rule provenance is frozen");
        Check.Equal(fixture.Calendar.Revision, issuedDay.CalendarRevision,
            "calendar revision provenance is frozen");

        var revisedCalendar = new RealmCalendar(
            new RealmId(1),
            "Etc/UTC",
            revision: 99,
            fixture.ReceivedAt,
            "test-revision");
        var rolledBackClock = new ManualTimeProvider();
        rolledBackClock.Advance(
            fixture.ReceivedAt - DateTimeOffset.UnixEpoch -
            TimeSpan.FromMinutes(1));
        var replayCoordinator = new MedusaAdmissionSagaCoordinator(
            revisedCalendar,
            rolledBackClock,
            fixture.Party,
            fixture.Store,
            fixture.Runtime,
            fixture.Transfer);
        var replay = await replayCoordinator.ExecuteAsync(fixture.Command);
        Check.True(
            replay.Status is MedusaAdmissionSagaStatus.Running or
                MedusaAdmissionSagaStatus.AlreadyRunning,
            "existing exact operation replays across calendar revision and clock rollback");
        Check.Equal(1, fixture.Party.Calls,
            "existing replay never reacquires or reinterprets party/day authority");
        Check.Equal(
            first.Admission!.RealmDay,
            replay.Admission!.RealmDay,
            "persisted calendar provenance remains authoritative on replay");
    }

    private static async Task AssertDeterministicTokenSurvivesLostEnsureReceiptAsync()
    {
        var fixture = new SagaFixture();
        var request = MedusaDurableAdmissionFoundationChecks.Request(
            fixture.Lease,
            fixture.ReceivedAt,
            MedusaEncounterDifficulty.Normal,
            fixture.AdmissionId,
            fixture.WorldInstanceId);
        var reserved = Reserved(request);
        var ensureRequest = new MedusaPendingStartRuntimeRequest(reserved);
        var first = await fixture.Runtime.EnsurePendingStartAsync(ensureRequest);
        fixture.Runtime.LoseProcess();
        var replay = await fixture.Runtime.EnsurePendingStartAsync(ensureRequest);
        Check.Equal(
            first.Snapshot!.TransferToken,
            replay.Snapshot!.TransferToken,
            "lost ensure receipt cannot mint a fresh runtime token");
    }

    private static MedusaAdmissionSnapshot Reserved(
        MedusaAdmissionReservationRequest request) =>
        new(
            request.AdmissionId,
            request.WorldInstanceId,
            request.RealmDay,
            request.Difficulty,
            request.ContentMapId,
            request.Source,
            request.Party,
            request.EncounterContentFingerprint,
            request.RosterHash,
            request.RequestHash,
            MedusaAdmissionState.Reserved,
            1,
            null,
            request.RequestedAtUtc,
            null,
            null,
            null,
            null,
            null);

    private sealed class SagaFixture
    {
        public const string ContentFingerprint =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        public SagaFixture()
        {
            ReceivedAt = new DateTimeOffset(
                2026, 8, 23, 12, 30, 0, TimeSpan.Zero);
            Source = new MedusaAdmissionSource(
                new WorldInstanceId(
                    Guid.Parse("30000000-0000-0000-0000-000000000001")),
                new MapId(60),
                5199);
            Lease = MedusaDurableAdmissionFoundationChecks.Lease(
                [
                    MedusaDurableAdmissionFoundationChecks.Member(10, 100, 1),
                    MedusaDurableAdmissionFoundationChecks.Member(20, 200, 2)
                ],
                ReceivedAt);
            AdmissionId = new MedusaAdmissionId(
                Guid.Parse("40000000-0000-0000-0000-000000000001"));
            WorldInstanceId = new WorldInstanceId(
                Guid.Parse("50000000-0000-0000-0000-000000000001"));
            Calendar = new RealmCalendar(
                new RealmId(1),
                "Pacific/Auckland",
                7,
                DateTimeOffset.UnixEpoch,
                "test-fixture");
            Clock.Advance(
                ReceivedAt - DateTimeOffset.UnixEpoch +
                TimeSpan.FromMinutes(4));
            Party = new MedusaSagaPartyAuthority(Lease, Events);
            Store = new MedusaSagaMemoryStore(Events);
            Runtime = new MedusaSagaRuntimeGateway(Events)
            {
                InitialPreparedAtUtc = ReceivedAt.AddMinutes(1)
            };
            Transfer = new MedusaSagaTransferGateway(Events)
            {
                PreparedAtUtc = ReceivedAt.AddMinutes(2),
                ExpiresAtUtc = ReceivedAt.AddMinutes(10),
                CommittedAtUtc = ReceivedAt.AddMinutes(3)
            };
            Command = new MedusaAdmissionStartCommand(
                new MedusaAdmissionOperationIdentity(
                    AdmissionId,
                    WorldInstanceId),
                MedusaEncounterDifficulty.Normal,
                Source,
                ContentFingerprint,
                Lease.LeaderAccountId,
                Lease.LeaderCharacterId,
                Lease.Members[0].Ownership,
                ReceivedAt);
            Coordinator = new MedusaAdmissionSagaCoordinator(
                Calendar,
                Clock,
                Party,
                Store,
                Runtime,
                Transfer);
        }

        public DateTimeOffset ReceivedAt { get; }
        public MedusaAdmissionSource Source { get; }
        public PartyAdmissionLease Lease { get; }
        public MedusaAdmissionId AdmissionId { get; }
        public WorldInstanceId WorldInstanceId { get; }
        public RealmCalendar Calendar { get; }
        public ManualTimeProvider Clock { get; } = new();
        public List<string> Events { get; } = [];
        public MedusaSagaPartyAuthority Party { get; }
        public MedusaSagaMemoryStore Store { get; }
        public MedusaSagaRuntimeGateway Runtime { get; }
        public MedusaSagaTransferGateway Transfer { get; }
        public MedusaAdmissionStartCommand Command { get; }
        public MedusaAdmissionSagaCoordinator Coordinator { get; }
    }
}
