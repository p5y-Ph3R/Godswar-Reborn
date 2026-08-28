using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMedusaTitleAwardStoreChecks
{
    private static async Task AssertAtomicRosterAwardAndReplayAsync(
        NpgsqlDataSource dataSource,
        PostgresMedusaDurableAdmissionStore admissionStore,
        PostgresMedusaTitleAwardStore titleStore)
    {
        var fixture = await CreateConsumedAsync(
            admissionStore,
            MedusaEncounterDifficulty.Enhanced,
            At(1),
            (10, 101),
            (20, 201),
            (30, 301));
        var request = Completion(fixture, TimeSpan.FromMinutes(10));

        var applied = await titleStore.SettleCompletionAsync(request);
        var replay = await titleStore.SettleCompletionAsync(request);
        CheckStatus(
            MedusaTitleSettlementStatus.Applied,
            applied.Status,
            "first title settlement applies atomically");
        CheckStatus(
            MedusaTitleSettlementStatus.Duplicate,
            replay.Status,
            "exact title settlement replay is idempotent");
        Check.True(
            string.Equals(
                MedusaTitleAwardPolicy.ChallengersKey,
                applied.Snapshot?.AwardedTitle?.Value,
                StringComparison.Ordinal),
            "inclusive ten-minute completion stores only Challengers");

        var admissionReplay = await admissionStore.TransitionAsync(
            new MedusaAdmissionTransitionRequest(
                request.OperationId,
                request.AdmissionId,
                MedusaAdmissionState.ConsumedRunning,
                MedusaAdmissionState.Completed,
                request.CompletedAtUtc));
        Check.True(
            admissionReplay.Status == MedusaAdmissionReceiptStatus.Duplicate &&
            admissionReplay.CommittedState == MedusaAdmissionState.Completed &&
            admissionReplay.CommittedRevision == 5 &&
            admissionReplay.Snapshot is
            {
                State: MedusaAdmissionState.Completed,
                Revision: 5
            } replayedAdmission &&
            replayedAdmission.TerminalAtUtc == request.CompletedAtUtc,
            "title settlement preserves exact admission-transition replay identity");

        foreach (var member in fixture.Reservation.Party.Members)
        {
            var titles = await titleStore.FindOwnershipAsync(
                member.CharacterId);
            Check.True(
                titles.Count == 1 &&
                titles[0].SemanticKey.Value ==
                    MedusaTitleAwardPolicy.ChallengersKey &&
                titles[0].SourceAdmissionId ==
                    fixture.Reservation.AdmissionId &&
                titles[0].AcquiredAtUtc == request.CompletedAtUtc,
                $"frozen member {member.CharacterId} owns the settled title");
        }
        Check.Equal(
            3L,
            await CountOwnershipRowsAsync(
                dataSource,
                fixture.Reservation.AdmissionId),
            "one transaction records ownership for the complete frozen roster");
        var terminal = await admissionStore.FindAsync(
            fixture.Reservation.AdmissionId);
        Check.True(
            terminal?.State == MedusaAdmissionState.Completed &&
            terminal.Revision == 5 &&
            terminal.TerminalAtUtc == request.CompletedAtUtc,
            "title settlement atomically terminalizes admission as Completed");

        var changedReplay = Completion(
            fixture,
            TimeSpan.FromMinutes(10).Add(TimeSpan.FromMicroseconds(1)));
        CheckStatus(
            MedusaTitleSettlementStatus.RequestConflict,
            (await titleStore.SettleCompletionAsync(changedReplay)).Status,
            "completion operation identity rejects changed replay evidence");
    }

    private static async Task AssertNoTitleSettlementAsync(
        PostgresMedusaDurableAdmissionStore admissionStore,
        PostgresMedusaTitleAwardStore titleStore)
    {
        var fixture = await CreateConsumedAsync(
            admissionStore,
            MedusaEncounterDifficulty.Enhanced,
            At(2),
            (40, 401),
            (50, 501));
        var request = Completion(fixture, TimeSpan.FromMinutes(21));
        var receipt = await titleStore.SettleCompletionAsync(request);

        CheckStatus(
            MedusaTitleSettlementStatus.Applied,
            receipt.Status,
            "valid slow victory persists a no-title settlement");
        Check.True(
            receipt.Snapshot?.AwardedTitle is null,
            "slow victory stores an explicit nullable no-title result");
        foreach (var member in fixture.Reservation.Party.Members)
        {
            Check.Equal(
                0,
                (await titleStore.FindOwnershipAsync(member.CharacterId)).Count,
                "no-title settlement creates no ownership row");
        }
    }

    private static async Task AssertPreownedTitleStillCoversFrozenRosterAsync(
        PostgresMedusaDurableAdmissionStore admissionStore,
        PostgresMedusaTitleAwardStore titleStore)
    {
        var first = await CreateConsumedAsync(
            admissionStore,
            MedusaEncounterDifficulty.Enhanced,
            At(7),
            new DateOnly(2026, 8, 24),
            (120, 1201));
        var firstRequest = Completion(first, TimeSpan.FromMinutes(10));
        CheckStatus(
            MedusaTitleSettlementStatus.Applied,
            (await titleStore.SettleCompletionAsync(firstRequest)).Status,
            "first completion acquires title ownership");
        var cleanup = new MedusaAdmissionTransitionRequest(
            MedusaAdmissionSagaOperationIds.CleanupCompleted(
                first.Reservation.AdmissionId),
            first.Reservation.AdmissionId,
            MedusaAdmissionState.Completed,
            MedusaAdmissionState.CompletedCleaned,
            firstRequest.CompletedAtUtc.AddMinutes(1),
            cleanupEvidence: new MedusaAdmissionCleanupEvidence(
                first.Reservation.AdmissionId,
                MedusaAdmissionCleanupKind.TerminalEgress,
                MedusaAdmissionSagaOperationIds.RosterEgress(
                    first.Reservation.AdmissionId),
                MedusaAdmissionSagaOperationIds.RuntimeRetire(
                    first.Reservation.AdmissionId)));
        Check.True(
            (await admissionStore.TransitionAsync(cleanup)).IsSuccess,
            "cleaned first completion releases active-member routing");
        CheckStatus(
            MedusaTitleSettlementStatus.Duplicate,
            (await titleStore.SettleCompletionAsync(firstRequest)).Status,
            "exact title settlement remains replayable after terminal cleanup");

        var second = await CreateConsumedAsync(
            admissionStore,
            MedusaEncounterDifficulty.Enhanced,
            At(22),
            new DateOnly(2026, 8, 25),
            (120, 1201),
            (130, 1301));
        var secondRequest = Completion(second, TimeSpan.FromMinutes(10));
        CheckStatus(
            MedusaTitleSettlementStatus.Applied,
            (await titleStore.SettleCompletionAsync(secondRequest)).Status,
            "later roster settlement tolerates existing semantic ownership");
        var existing = await titleStore.FindOwnershipAsync(1201);
        var acquired = await titleStore.FindOwnershipAsync(1301);
        Check.True(
            existing.Count == 1 &&
            existing[0].SourceAdmissionId == first.Reservation.AdmissionId,
            "preowned title retains its immutable first acquisition source");
        Check.True(
            acquired.Count == 1 &&
            acquired[0].SourceAdmissionId == second.Reservation.AdmissionId,
            "new roster member acquires title from the later settlement");
    }

    private static async Task AssertEvidenceAndTerminalConflictsAsync(
        PostgresMedusaDurableAdmissionStore admissionStore,
        PostgresMedusaTitleAwardStore titleStore)
    {
        var fixture = await CreateConsumedAsync(
            admissionStore,
            MedusaEncounterDifficulty.Enhanced,
            At(3),
            (60, 601),
            (70, 701));
        var exact = Completion(fixture, TimeSpan.FromMinutes(12));

        foreach (var changed in ChangedEvidenceRequests(fixture, exact))
        {
            CheckStatus(
                MedusaTitleSettlementStatus.AdmissionEvidenceConflict,
                (await titleStore.SettleCompletionAsync(changed)).Status,
                "inexact immutable admission evidence fails closed");
        }
        Check.True(
            await titleStore.FindSettlementAsync(
                fixture.Reservation.AdmissionId) is null,
            "evidence conflicts leave no partial settlement");
        CheckStatus(
            MedusaTitleSettlementStatus.Applied,
            (await titleStore.SettleCompletionAsync(exact)).Status,
            "evidence conflicts do not consume the exact later settlement");

        var terminalFixture = await CreateConsumedAsync(
            admissionStore,
            MedusaEncounterDifficulty.Enhanced,
            At(4),
            (80, 801));
        var completed = new MedusaAdmissionTransitionRequest(
            MedusaAdmissionSagaOperationIds.Completed(
                terminalFixture.Reservation.AdmissionId),
            terminalFixture.Reservation.AdmissionId,
            MedusaAdmissionState.ConsumedRunning,
            MedusaAdmissionState.Completed,
            terminalFixture.ConsumedAtUtc.AddMinutes(1));
        Check.True(
            (await admissionStore.TransitionAsync(completed)).IsSuccess,
            "terminal-conflict fixture completes outside title settlement");
        CheckStatus(
            MedusaTitleSettlementStatus.TerminalConflict,
            (await titleStore.SettleCompletionAsync(
                Completion(
                    terminalFixture,
                    TimeSpan.FromMinutes(10)))).Status,
            "already-terminal admission cannot mint a late title");
        Check.Equal(
            0,
            (await titleStore.FindOwnershipAsync(801)).Count,
            "terminal conflict leaves title ownership unchanged");
    }

    private static async Task AssertConcurrentSettlementAsync(
        PostgresMedusaDurableAdmissionStore admissionStore,
        PostgresMedusaTitleAwardStore titleStore)
    {
        var fixture = await CreateConsumedAsync(
            admissionStore,
            MedusaEncounterDifficulty.Mythic,
            At(5),
            (90, 901),
            (100, 1001));
        var request = Completion(fixture, TimeSpan.FromMinutes(10));
        var receipts = await Task.WhenAll(
            titleStore.SettleCompletionAsync(request),
            titleStore.SettleCompletionAsync(request));
        Check.Equal(
            1,
            receipts.Count(receipt =>
                receipt.Status == MedusaTitleSettlementStatus.Applied),
            "concurrent exact settlement has one writer");
        Check.Equal(
            1,
            receipts.Count(receipt =>
                receipt.Status == MedusaTitleSettlementStatus.Duplicate),
            "concurrent exact settlement loser replays durable result");
        Check.True(
            string.Equals(
                MedusaTitleAwardPolicy.HeirOfPerseusKey,
                receipts[0].Snapshot?.AwardedTitle?.Value,
                StringComparison.Ordinal),
            "Mythic ownership persists semantic key despite absent stock ID");
    }

    private static async Task AssertChangedConcurrentRequestConflictsAsync(
        PostgresMedusaDurableAdmissionStore admissionStore,
        PostgresMedusaTitleAwardStore titleStore)
    {
        var fixture = await CreateConsumedAsync(
            admissionStore,
            MedusaEncounterDifficulty.Enhanced,
            At(6),
            (110, 1101));
        var fast = Completion(fixture, TimeSpan.FromMinutes(10));
        var slower = Completion(fixture, TimeSpan.FromMinutes(11));
        var receipts = await Task.WhenAll(
            titleStore.SettleCompletionAsync(fast),
            titleStore.SettleCompletionAsync(slower));
        Check.Equal(
            1,
            receipts.Count(receipt =>
                receipt.Status == MedusaTitleSettlementStatus.Applied),
            "changed concurrent completion has one atomic winner");
        Check.Equal(
            1,
            receipts.Count(receipt =>
                receipt.Status == MedusaTitleSettlementStatus.RequestConflict),
            "changed concurrent completion rejects the losing evidence");
        Check.True(
            receipts[0].Snapshot is { } left &&
            receipts[1].Snapshot is { } right &&
            string.Equals(
                left.RequestHash,
                right.RequestHash,
                StringComparison.Ordinal),
            "both concurrent receipts expose the same durable winner");
    }
}
