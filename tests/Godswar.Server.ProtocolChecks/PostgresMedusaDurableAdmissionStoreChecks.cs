using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMedusaDurableAdmissionStoreChecks
{
    public const string CheckName =
        "PostgreSQL Medusa durable admission claims";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} ({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var database = await ReadDatabaseAsync(dataSource);
        if (!PostgresMedusaAdmissionSchema.IsDisposableDatabaseName(database))
        {
            Console.WriteLine(
                $"SKIP {CheckName} (database '{database}' is not disposable)");
            return;
        }

        var wrongDatabase = database == "godswar_medusa_ffffffff"
            ? "godswar_medusa_eeeeeeee"
            : "godswar_medusa_ffffffff";
        await AssertThrowsAsync<InvalidOperationException>(
            () => PostgresMedusaAdmissionSchema.CreateForDisposableDatabaseAsync(
                dataSource,
                wrongDatabase),
            "schema mutation rejects an inexact current database name");

        await PostgresMedusaAdmissionSchema.DropForDisposableDatabaseAsync(
            dataSource,
            database);
        await PostgresMedusaAdmissionSchema.CreateForDisposableDatabaseAsync(
            dataSource,
            database);
        try
        {
            var store = new PostgresMedusaDurableAdmissionStore(dataSource);
            await AssertReservationReplayAndConflictAsync(store);
            await AssertTransitionsAndTerminalConsumptionAsync(
                dataSource,
                store);
            await AssertConcurrentRosterClaimsAreAtomicAsync(
                dataSource,
                store);
            await AssertCrossBoundaryAndInverseOrderClaimsAsync(
                store);
            await AssertPartialConsumptionRollsBackAsync(
                dataSource,
                store);
        }
        finally
        {
            await PostgresMedusaAdmissionSchema.DropForDisposableDatabaseAsync(
                dataSource,
                database);
        }
    }

    private static async Task AssertReservationReplayAndConflictAsync(
        PostgresMedusaDurableAdmissionStore store)
    {
        var at = MedusaDurableAdmissionFoundationChecks.Utc(5);
        var lease = Lease(at, (10, 100), (20, 200));
        var request = Request(lease, at);
        var first = await store.ReserveAsync(request);
        var replay = await store.ReserveAsync(request);
        CheckStatus(
            MedusaAdmissionReceiptStatus.Applied,
            first.Status,
            "first frozen roster reservation is applied");
        CheckStatus(
            MedusaAdmissionReceiptStatus.Duplicate,
            replay.Status,
            "exact reservation replay is idempotent");
        Check.True(
            first.Snapshot?.RealmDay.TimeZoneRulesFingerprint ==
                request.RealmDay.TimeZoneRulesFingerprint,
            "timezone-rule fingerprint round-trips as durable day provenance");

        var changed = new MedusaAdmissionReservationRequest(
            request.AdmissionId,
            WorldInstanceId.New(),
            request.RealmDay,
            request.Difficulty,
            request.Source,
            request.Party,
            request.EncounterContentFingerprint,
            request.RequestedAtUtc);
        var requestConflict = await store.ReserveAsync(changed);
        CheckStatus(
            MedusaAdmissionReceiptStatus.RequestConflict,
            requestConflict.Status,
            "reused admission identity rejects changed target identity");

        var colliding = Request(Lease(at, (20, 200), (30, 300)), at);
        var memberConflict = await store.ReserveAsync(colliding);
        CheckStatus(
            MedusaAdmissionReceiptStatus.MemberAttemptConflict,
            memberConflict.Status,
            "one claimed member rejects the whole frozen roster");
        Check.True(
            await store.FindAsync(colliding.AdmissionId) is null,
            "member conflict rolls back the admission row");

        var unclaimedMember = Request(Lease(at, (30, 300)), at);
        CheckStatus(
            MedusaAdmissionReceiptStatus.Applied,
            (await store.ReserveAsync(unclaimedMember)).Status,
            "failed multi-member reservation leaves no partial member claim");
        var release = new MedusaAdmissionTransitionRequest(
            Guid.NewGuid(),
            unclaimedMember.AdmissionId,
            MedusaAdmissionState.Reserved,
            MedusaAdmissionState.Released,
            at.AddMinutes(1));
        CheckStatus(
            MedusaAdmissionReceiptStatus.Applied,
            (await store.TransitionAsync(release)).Status,
            "pre-runtime reservation can release all claims");
        CheckStatus(
            MedusaAdmissionReceiptStatus.Applied,
            (await store.TransitionAsync(new MedusaAdmissionTransitionRequest(
                MedusaAdmissionSagaOperationIds.CleanupCompleted(
                    unclaimedMember.AdmissionId),
                unclaimedMember.AdmissionId,
                MedusaAdmissionState.Released,
                MedusaAdmissionState.ReleasedCleaned,
                at.AddMinutes(1),
                cleanupEvidence: CleanupEvidence(
                    unclaimedMember.AdmissionId,
                    MedusaAdmissionCleanupKind.PreBarrierRelease)))).Status,
            "release cleanup receipts retire the active-member assignment");
        var reusedMember = Request(Lease(at, (30, 300)), at.AddMinutes(2));
        CheckStatus(
            MedusaAdmissionReceiptStatus.Applied,
            (await store.ReserveAsync(reusedMember)).Status,
            "released reservation no longer blocks a new admission");
    }

    private static async Task AssertTransitionsAndTerminalConsumptionAsync(
        NpgsqlDataSource dataSource,
        PostgresMedusaDurableAdmissionStore store)
    {
        var admission = await FindByCharacterAsync(dataSource, 100);
        var at = MedusaDurableAdmissionFoundationChecks.Utc(6);
        var staleRelease = new MedusaAdmissionTransitionRequest(
            Guid.NewGuid(),
            admission,
            MedusaAdmissionState.Reserved,
            MedusaAdmissionState.Released,
            at.AddMinutes(3));
        var readyId = Guid.NewGuid();
        var ready = new MedusaAdmissionTransitionRequest(
            readyId,
            admission,
            MedusaAdmissionState.Reserved,
            MedusaAdmissionState.RuntimeReady,
            at);
        CheckStatus(
            MedusaAdmissionReceiptStatus.Applied,
            (await store.TransitionAsync(ready)).Status,
            "runtime-ready transition is applied");
        CheckStatus(
            MedusaAdmissionReceiptStatus.Duplicate,
            (await store.TransitionAsync(ready)).Status,
            "exact transition replay returns its durable receipt");

        var changedReplay = new MedusaAdmissionTransitionRequest(
            readyId,
            admission,
            MedusaAdmissionState.Reserved,
            MedusaAdmissionState.RuntimeReady,
            at.AddMinutes(1));
        CheckStatus(
            MedusaAdmissionReceiptStatus.RequestConflict,
            (await store.TransitionAsync(changedReplay)).Status,
            "transition identity rejects a changed replay");
        CheckStatus(
            MedusaAdmissionReceiptStatus.InvalidTransition,
            (await store.TransitionAsync(staleRelease)).Status,
            "stale expected state cannot release a newer runtime");

        var transferCommitted = new MedusaAdmissionTransitionRequest(
            Guid.NewGuid(),
            admission,
            MedusaAdmissionState.RuntimeReady,
            MedusaAdmissionState.RosterTransferCommitted,
            at.AddMinutes(1),
            Barrier());
        var consumed = new MedusaAdmissionTransitionRequest(
            Guid.NewGuid(),
            admission,
            MedusaAdmissionState.RosterTransferCommitted,
            MedusaAdmissionState.ConsumedRunning,
            at.AddMinutes(2));
        CheckStatus(
            MedusaAdmissionReceiptStatus.Applied,
            (await store.TransitionAsync(transferCommitted)).Status,
            "irreversible roster-transfer barrier becomes durable");
        CheckStatus(
            MedusaAdmissionReceiptStatus.Applied,
            (await store.TransitionAsync(consumed)).Status,
            "all frozen attempts are consumed with running state");
        var completed = new MedusaAdmissionTransitionRequest(
            Guid.NewGuid(),
            admission,
            MedusaAdmissionState.ConsumedRunning,
            MedusaAdmissionState.Completed,
            at.AddMinutes(3));
        CheckStatus(
            MedusaAdmissionReceiptStatus.Applied,
            (await store.TransitionAsync(completed)).Status,
            "consumed run records a typed immutable terminal outcome");
        Check.Equal(
            2L,
            await CountClaimsAsync(dataSource, admission, claimState: 2),
            "terminal completion retains every consumed roster claim");
        CheckState(
            MedusaAdmissionState.Completed,
            (await store.FindAsync(admission))!.State,
            "completed state is terminal durable evidence");
        Check.True(
            await store.FindActiveByMemberAsync(new RealmId(1), 100) is not null,
            "terminal assignment remains recoverable before egress/retire");
        await AssertCleanupKindConstraintAsync(dataSource, admission);
        var cleaned = new MedusaAdmissionTransitionRequest(
            MedusaAdmissionSagaOperationIds.CleanupCompleted(admission),
            admission,
            MedusaAdmissionState.Completed,
            MedusaAdmissionState.CompletedCleaned,
            at.AddMinutes(4),
            cleanupEvidence: CleanupEvidence(
                admission,
                MedusaAdmissionCleanupKind.TerminalEgress));
        CheckStatus(
            MedusaAdmissionReceiptStatus.Applied,
            (await store.TransitionAsync(cleaned)).Status,
            "terminal cleanup durably records exact egress and retire receipts");
        Check.True(
            await store.FindActiveByMemberAsync(new RealmId(1), 100) is null,
            "cleaned terminal admission releases exact active routing");
    }

    private static async Task AssertConcurrentRosterClaimsAreAtomicAsync(
        NpgsqlDataSource dataSource,
        PostgresMedusaDurableAdmissionStore store)
    {
        var at = MedusaDurableAdmissionFoundationChecks.Utc(15);
        var left = Request(Lease(at, (60, 600), (70, 700)), at);
        var right = Request(Lease(at, (60, 600), (80, 800)), at);
        var results = await Task.WhenAll(
            store.ReserveAsync(left),
            store.ReserveAsync(right));
        Check.Equal(
            1,
            results.Count(result =>
                result.Status == MedusaAdmissionReceiptStatus.Applied),
            "concurrent overlapping rosters have exactly one winner");
        Check.Equal(
            1,
            results.Count(result =>
                result.Status ==
                    MedusaAdmissionReceiptStatus.MemberAttemptConflict),
            "concurrent overlapping roster loser gets a member conflict");
        Check.Equal(
            2L,
            await CountClaimsForCharactersAsync(dataSource, 600, 700, 800),
            "concurrent loser leaves none of its roster claims behind");
        Check.Equal(
            1,
            new[] { left, right }.Count(request =>
                store.FindAsync(request.AdmissionId).GetAwaiter().GetResult()
                    is not null),
            "concurrent loser leaves no admission publication behind");
    }

    private static async Task AssertPartialConsumptionRollsBackAsync(
        NpgsqlDataSource dataSource,
        PostgresMedusaDurableAdmissionStore store)
    {
        var at = MedusaDurableAdmissionFoundationChecks.Utc(25);
        var request = Request(Lease(at, (90, 900), (91, 901)), at);
        CheckStatus(
            MedusaAdmissionReceiptStatus.Applied,
            (await store.ReserveAsync(request)).Status,
            "corruption fixture reserves a complete roster");
        await store.TransitionAsync(new MedusaAdmissionTransitionRequest(
            Guid.NewGuid(),
            request.AdmissionId,
            MedusaAdmissionState.Reserved,
            MedusaAdmissionState.RuntimeReady,
            at.AddMinutes(1)));
        await store.TransitionAsync(new MedusaAdmissionTransitionRequest(
            Guid.NewGuid(),
            request.AdmissionId,
            MedusaAdmissionState.RuntimeReady,
            MedusaAdmissionState.RosterTransferCommitted,
            at.AddMinutes(2),
            Barrier()));

        await DeleteClaimAsync(dataSource, request.AdmissionId, 901);
        var consume = new MedusaAdmissionTransitionRequest(
            Guid.NewGuid(),
            request.AdmissionId,
            MedusaAdmissionState.RosterTransferCommitted,
            MedusaAdmissionState.ConsumedRunning,
            at.AddMinutes(3));
        await AssertThrowsAsync<InvalidDataException>(
            () => store.TransitionAsync(consume),
            "partial roster cannot be consumed");
        CheckState(
            MedusaAdmissionState.RosterTransferCommitted,
            (await store.FindAsync(request.AdmissionId))!.State,
            "failed consumption preserves the transfer barrier");
        Check.Equal(
            1L,
            await CountClaimsAsync(
                dataSource,
                request.AdmissionId,
                claimState: 1),
            "failed consumption rolls back the surviving claim mutation");
    }

    private static PartyAdmissionLease Lease(
        DateTimeOffset at,
        params (int AccountId, int CharacterId)[] members) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            members[0].AccountId,
            members[0].CharacterId,
            members.Select((member, index) =>
                MedusaDurableAdmissionFoundationChecks.Member(
                    member.AccountId,
                    member.CharacterId,
                    index + 1)),
            at,
            at.AddMinutes(10));

    private static MedusaAdmissionReservationRequest Request(
        PartyAdmissionLease party,
        DateTimeOffset at) =>
        MedusaDurableAdmissionFoundationChecks.Request(
            party,
            at,
            MedusaEncounterDifficulty.Enhanced);

}
