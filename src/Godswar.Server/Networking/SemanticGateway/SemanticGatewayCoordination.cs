using System.Net;
using Godswar.Server.Application.Coordination;

namespace Godswar.Server.Networking.SemanticGateway;

/// <summary>
/// B18C2-compatible in-process implementation of the asynchronous B17 seam.
/// Calls remain allocation-free after the returned ValueTask and preserve the
/// existing static route and admission authority behavior exactly.
/// </summary>
internal sealed class InMemorySemanticGatewayCoordination :
    ISemanticGatewayCoordination
{
    private readonly SemanticGatewayAdmissionAuthority _authority;
    private readonly TimeProvider _timeProvider;

    public InMemorySemanticGatewayCoordination(
        SemanticGatewayAdmissionAuthority authority,
        TimeProvider? timeProvider = null)
    {
        _authority = authority ??
            throw new ArgumentNullException(nameof(authority));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<SemanticGatewayLoginResult> StartLoginAsync(
        SemanticGatewayPrincipal principal,
        SemanticGatewayConnectionSource loginSource,
        SemanticGatewayRealmGrant realmGrant,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken)
    {
        EnsureAvailable(deadline, cancellationToken);
        return ValueTask.FromResult(
            _authority.BeginLogin(principal, loginSource, realmGrant));
    }

    public ValueTask<bool> ActivateLoginAsync(
        SemanticGatewayLoginGenerationLease generation,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken)
    {
        EnsureAvailable(deadline, cancellationToken);
        return ValueTask.FromResult(
            _authority.ActivateLogin(generation));
    }

    public ValueTask<bool> CancelLoginAsync(
        SemanticGatewayLoginGenerationLease generation,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken)
    {
        EnsureAvailable(deadline, cancellationToken);
        return ValueTask.FromResult(
            _authority.CancelLogin(generation));
    }

    public ValueTask<SemanticGatewayLoginLookupResult>
        FindActivatedLoginAsync(
            string canonicalUsername,
            IPAddress observedGameAddress,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        EnsureAvailable(deadline, cancellationToken);
        return ValueTask.FromResult(
            _authority.TryFindLogin(
                canonicalUsername,
                observedGameAddress));
    }

    public ValueTask<SemanticGatewayAdmissionResult>
        ReserveAdmissionAsync(
            GatewayLoginGenerationId generationId,
            SemanticGatewayPrincipal principal,
            SemanticGatewayConnectionSource source,
            SemanticGatewayRouteTarget target,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        EnsureAvailable(deadline, cancellationToken);
        return ValueTask.FromResult(
            _authority.Reserve(
                generationId,
                principal,
                source,
                target));
    }

    public ValueTask<SemanticGatewayAdmissionResult>
        CommitAdmissionAsync(
            SemanticGatewayAdmissionClaim claim,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        EnsureAvailable(deadline, cancellationToken);
        return ValueTask.FromResult(_authority.Commit(claim));
    }

    public ValueTask<SemanticGatewayAdmissionResult>
        RefreshAdmissionAsync(
            SemanticGatewayAdmissionClaim claim,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        EnsureAvailable(deadline, cancellationToken);
        return ValueTask.FromResult(
            _authority.RefreshCommitted(claim));
    }

    public ValueTask<SemanticGatewayAdmissionResult>
        ResolveAdmissionAsync(
            SemanticGatewayAdmissionClaim claim,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        EnsureAvailable(deadline, cancellationToken);
        return ValueTask.FromResult(
            _authority.ResolveCommitted(claim));
    }

    public ValueTask<SemanticGatewayAdmissionResult>
        RollbackAdmissionAsync(
            SemanticGatewayAdmissionClaim claim,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        EnsureAvailable(deadline, cancellationToken);
        return ValueTask.FromResult(_authority.Rollback(claim));
    }

    public ValueTask<SemanticGatewayAdmissionResult>
        ReleaseAdmissionAsync(
            SemanticGatewayAdmissionClaim claim,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        EnsureAvailable(deadline, cancellationToken);
        return ValueTask.FromResult(_authority.Release(claim));
    }

    public ValueTask<int> SweepExpiredAsync(
        CoordinationDeadline deadline,
        CancellationToken cancellationToken)
    {
        EnsureAvailable(deadline, cancellationToken);
        return ValueTask.FromResult(_authority.SweepExpired());
    }

    public ValueTask DisposeAsync() =>
        ValueTask.CompletedTask;

    public SemanticGatewayAuthoritySnapshot GetSnapshot() =>
        _authority.GetSnapshot();

    private void EnsureAvailable(
        CoordinationDeadline deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.Remaining(_timeProvider) <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                "The semantic-gateway coordination deadline elapsed.");
        }
    }
}
