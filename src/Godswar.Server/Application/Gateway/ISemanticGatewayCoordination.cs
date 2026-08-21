using System.Net;
using Godswar.Server.Application.Coordination;

namespace Godswar.Server.Application.Gateway;

/// <summary>
/// Intent-specific, disposable semantic-gateway coordination. Durable
/// character state and PostgreSQL ownership fences do not belong here.
/// </summary>
internal interface ISemanticGatewayCoordination : IAsyncDisposable
{
    ValueTask<SemanticGatewayLoginResult> StartLoginAsync(
        SemanticGatewayPrincipal principal,
        SemanticGatewayConnectionSource loginSource,
        SemanticGatewayRealmGrant realmGrant,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken);

    ValueTask<bool> ActivateLoginAsync(
        SemanticGatewayLoginGenerationLease generation,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken);

    ValueTask<bool> CancelLoginAsync(
        SemanticGatewayLoginGenerationLease generation,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken);

    ValueTask<SemanticGatewayLoginLookupResult>
        FindActivatedLoginAsync(
            string canonicalUsername,
            IPAddress observedGameAddress,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken);

    ValueTask<SemanticGatewayAdmissionResult> ReserveAdmissionAsync(
        GatewayLoginGenerationId generationId,
        SemanticGatewayPrincipal principal,
        SemanticGatewayConnectionSource source,
        SemanticGatewayRouteTarget target,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken);

    ValueTask<SemanticGatewayAdmissionResult> CommitAdmissionAsync(
        SemanticGatewayAdmissionClaim claim,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken);

    ValueTask<SemanticGatewayAdmissionResult> RefreshAdmissionAsync(
        SemanticGatewayAdmissionClaim claim,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken);

    ValueTask<SemanticGatewayAdmissionResult> ResolveAdmissionAsync(
        SemanticGatewayAdmissionClaim claim,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken);

    ValueTask<SemanticGatewayAdmissionResult> RollbackAdmissionAsync(
        SemanticGatewayAdmissionClaim claim,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken);

    ValueTask<SemanticGatewayAdmissionResult> ReleaseAdmissionAsync(
        SemanticGatewayAdmissionClaim claim,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken);

    ValueTask<int> SweepExpiredAsync(
        CoordinationDeadline deadline,
        CancellationToken cancellationToken);

    SemanticGatewayAuthoritySnapshot GetSnapshot();
}
