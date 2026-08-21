using Godswar.Server.Application.Coordination;
using Godswar.Server.Networking.SemanticGateway;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SemanticGatewayChecks
{
    private static async Task CheckAsyncCoordinationBoundaryAsync()
    {
        var time = new ManualTimeProvider();
        var authority = new SemanticGatewayAdmissionAuthority(
            CreateDirectory(),
            new SemanticGatewayAuthorityLimits(
                maximumLoginGenerations: 8,
                maximumAdmissions: 8,
                maximumAdmissionsPerGeneration: 1,
                loginGenerationTtl: TimeSpan.FromMinutes(2),
                reservationTtl: TimeSpan.FromSeconds(30),
                committedAdmissionTtl: TimeSpan.FromMinutes(1)),
            time);
        await using ISemanticGatewayCoordination coordination =
            new InMemorySemanticGatewayCoordination(authority, time);
        var principal =
            new SemanticGatewayPrincipal(707, "ASYNC");
        var loginSource = Source();
        var deadline = () =>
            CoordinationDeadline.FromNow(
                TimeSpan.FromSeconds(2),
                time);

        var login = await coordination.StartLoginAsync(
            principal,
            loginSource,
            SemanticGatewayTestRealm.TempestGrant,
            deadline(),
            CancellationToken.None);
        Check.True(
            login.IsStarted &&
            (await coordination.FindActivatedLoginAsync(
                "ASYNC",
                loginSource.Address!,
                deadline(),
                CancellationToken.None)).Status ==
                    SemanticGatewayLoginLookupStatus.NotActivated,
            "async coordination preserves pending login visibility");
        Check.True(
            await coordination.ActivateLoginAsync(
                login.Generation!,
                deadline(),
                CancellationToken.None),
            "async coordination activates the exact login generation");
        var found = await coordination.FindActivatedLoginAsync(
            "ASYNC",
            loginSource.Address!,
            deadline(),
            CancellationToken.None);
        var reserved = await coordination.ReserveAdmissionAsync(
            found.Generation!.GenerationId,
            principal,
            Source(),
            Target(Sparta, SpartaInstance),
            deadline(),
            CancellationToken.None);
        var claim = Claim(reserved.Admission!);
        Check.True(
            (await coordination.CommitAdmissionAsync(
                claim,
                deadline(),
                CancellationToken.None)).Status ==
                    SemanticGatewayAdmissionStatus.Committed &&
            (await coordination.ResolveAdmissionAsync(
                claim,
                deadline(),
                CancellationToken.None)).Status ==
                    SemanticGatewayAdmissionStatus.Committed &&
            (await coordination.RefreshAdmissionAsync(
                claim,
                deadline(),
                CancellationToken.None)).Status ==
                    SemanticGatewayAdmissionStatus.Refreshed &&
            (await coordination.ReleaseAdmissionAsync(
                claim,
                deadline(),
                CancellationToken.None)).Status ==
                    SemanticGatewayAdmissionStatus.Released,
            "async coordination preserves reserve, commit, refresh, " +
            "resolve, and release semantics");

        var rollbackLogin = await coordination.StartLoginAsync(
            principal,
            Source(),
            SemanticGatewayTestRealm.TempestGrant,
            deadline(),
            CancellationToken.None);
        _ = await coordination.ActivateLoginAsync(
            rollbackLogin.Generation!,
            deadline(),
            CancellationToken.None);
        var rollback = await coordination.ReserveAdmissionAsync(
            rollbackLogin.Generation!.GenerationId,
            principal,
            Source(),
            Target(Sparta, SpartaInstance),
            deadline(),
            CancellationToken.None);
        Check.True(
            (await coordination.RollbackAdmissionAsync(
                Claim(rollback.Admission!),
                deadline(),
                CancellationToken.None)).Status ==
                    SemanticGatewayAdmissionStatus.RolledBack,
            "async coordination preserves reservation rollback");

        var cancelPrincipal =
            new SemanticGatewayPrincipal(708, "CANCEL");
        var cancellable = await coordination.StartLoginAsync(
            cancelPrincipal,
            Source(),
            SemanticGatewayTestRealm.TempestGrant,
            deadline(),
            CancellationToken.None);
        Check.True(
            await coordination.CancelLoginAsync(
                cancellable.Generation!,
                deadline(),
                CancellationToken.None),
            "async coordination cancels the exact login generation");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var observedCancellation = false;
        try
        {
            _ = await coordination.StartLoginAsync(
                new SemanticGatewayPrincipal(709, "CANCELLED"),
                Source(),
                SemanticGatewayTestRealm.TempestGrant,
                deadline(),
                cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            observedCancellation = true;
        }
        Check.True(
            observedCancellation,
            "async coordination observes caller cancellation before mutation");

        var observedDeadline = false;
        try
        {
            _ = await coordination.StartLoginAsync(
                new SemanticGatewayPrincipal(710, "EXPIRED"),
                Source(),
                SemanticGatewayTestRealm.TempestGrant,
                new CoordinationDeadline(
                    time.GetUtcNow()),
                CancellationToken.None);
        }
        catch (TimeoutException)
        {
            observedDeadline = true;
        }
        Check.True(
            observedDeadline,
            "async coordination rejects an elapsed absolute deadline");

        time.Advance(TimeSpan.FromMinutes(3));
        Check.True(
            await coordination.SweepExpiredAsync(
                deadline(),
                CancellationToken.None) > 0 &&
            coordination.GetSnapshot().ActiveLoginGenerations == 0,
            "async bounded sweep reclaims in-memory coordination expiry");
    }
}
