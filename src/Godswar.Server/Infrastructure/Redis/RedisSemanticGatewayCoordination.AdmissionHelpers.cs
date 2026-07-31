using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Gateway;
using StackExchange.Redis;

namespace Godswar.Server.Infrastructure.Redis;

internal sealed partial class RedisSemanticGatewayCoordination
{
    private ValueTask<RedisResult> ExecuteClaimScriptAsync(
        string script,
        SemanticGatewayAdmissionClaim claim,
        RedisValue[] suffix,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken)
    {
        var arguments = ClaimArguments(claim);
        if (suffix.Length != 0)
        {
            arguments = [.. arguments, .. suffix];
        }
        return _executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            deadline,
            database => database.ScriptEvaluateAsync(
                script,
                [
                    _keys.Admission(claim.AdmissionId.Value),
                    _keys.LoginAccount(claim.Principal.AccountId),
                    _keys.LoginName(
                        claim.Principal.CanonicalUsername!),
                    _keys.LoginConnection(
                        claim.Source.ConnectionId.Value),
                    _keys.GatewayCounters(),
                    _keys.GatewayExpiry()
                ],
                arguments),
            cancellationToken);
    }

    private ValueTask<RedisResult> ExecuteProvenClaimScriptAsync(
        string script,
        SemanticGatewayAdmissionClaim claim,
        RedisSemanticGatewayRouteProof proof,
        RedisValue[] suffix,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken)
    {
        if (!proof.IsSelected ||
            proof.CoordinatedRoute is not { } coordinated)
        {
            throw new ArgumentException(
                "An exact coordinated route proof is required.",
                nameof(proof));
        }

        var arguments = ClaimArguments(claim);
        arguments =
        [
            .. arguments,
            .. suffix,
            coordinated.BootId.ToString("N"),
            coordinated.Revision
        ];
        return _executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            deadline,
            database => database.ScriptEvaluateAsync(
                script,
                [
                    _keys.Admission(claim.AdmissionId.Value),
                    _keys.LoginAccount(claim.Principal.AccountId),
                    _keys.LoginName(
                        claim.Principal.CanonicalUsername!),
                    _keys.LoginConnection(
                        claim.Source.ConnectionId.Value),
                    _keys.GatewayCounters(),
                    _keys.GatewayExpiry(),
                    _keys.Route(claim.Target.WorldInstanceId),
                    _keys.Worker(claim.NodeId)
                ],
                arguments),
            cancellationToken);
    }

    private static RedisValue[] ClaimArguments(
        SemanticGatewayAdmissionClaim claim) =>
        [
            claim.AdmissionId.ToString(),
            claim.GenerationId.ToString(),
            claim.Principal.AccountId,
            claim.Principal.CanonicalUsername!,
            claim.Source.ConnectionId.ToString(),
            claim.Source.Address!.ToString(),
            claim.Target.RealmId.Value,
            claim.Target.MapId.Value,
            claim.Target.WorldInstanceId.Value.ToString("N"),
            claim.NodeId.ToString(),
            claim.WorkerRevision
        ];

    private static SemanticGatewayRouteSelection Selection(
        SemanticGatewayAdmissionClaim claim) =>
        new(claim.Target, claim.NodeId, claim.WorkerRevision);

    private static SemanticGatewayAdmissionResult ParseAdmissionResult(
        RedisResult result,
        SemanticGatewayAdmissionClaim claim)
    {
        var values =
            RedisSemanticGatewayResultReader.Array(result, 4);
        return ParseAdmissionValues(values, claim);
    }

    private static SemanticGatewayAdmissionResult
        ParseProvenAdmissionResult(
            RedisResult result,
            SemanticGatewayAdmissionClaim claim)
    {
        var values =
            RedisSemanticGatewayResultReader.Array(result, 5);
        var parsed = ParseAdmissionValues(values, claim);
        var rejectionValue =
            RedisSemanticGatewayResultReader.Int64(values[4]);
        return rejectionValue == 0
            ? parsed
            : parsed with
            {
                RouteRejection = RouteStatus(values[4])
            };
    }

    private static RedisSemanticGatewayRefreshResult
        ParseRefreshAdmissionResult(
            RedisResult result,
            SemanticGatewayAdmissionClaim claim)
    {
        var values =
            RedisSemanticGatewayResultReader.Array(result, 6);
        var parsed = ParseAdmissionValues(values, claim);
        var rejectionValue =
            RedisSemanticGatewayResultReader.Int64(values[4]);
        if (rejectionValue != 0)
        {
            parsed = parsed with
            {
                RouteRejection = RouteStatus(values[4])
            };
        }

        return new(
            parsed,
            RedisSemanticGatewayResultReader.Timestamp(values[5]));
    }

    private static SemanticGatewayAdmissionResult ParseAdmissionValues(
        RedisResult[] values,
        SemanticGatewayAdmissionClaim claim)
    {
        var status = AdmissionStatus(values[0]);
        var stateValue =
            RedisSemanticGatewayResultReader.Int64(values[3]);
        if (stateValue == 0)
        {
            return new(status, null);
        }
        if (stateValue is not (1 or 2))
        {
            throw new InvalidDataException(
                "Redis returned an invalid admission lifecycle state.");
        }

        var state = (SemanticGatewayAdmissionState)stateValue;
        var lease = new SemanticGatewayAdmissionLease(
            claim.AdmissionId,
            claim.GenerationId,
            claim.Principal,
            claim.Source,
            Selection(claim),
            state,
            RedisSemanticGatewayResultReader.Timestamp(values[1]),
            RedisSemanticGatewayResultReader.Timestamp(values[2]));
        return new(status, lease);
    }

    private void ObserveAdmissionResult(
        SemanticGatewayAdmissionResult result,
        SemanticGatewayAdmissionClaim claim,
        bool countSuccess = true)
    {
        if (result.Admission is not null &&
            result.Status is
                SemanticGatewayAdmissionStatus.Committed or
                SemanticGatewayAdmissionStatus.Refreshed or
                SemanticGatewayAdmissionStatus.StateConflict)
        {
            _admissions[claim.AdmissionId] = result.Admission;
        }
        if (result.Status is
            SemanticGatewayAdmissionStatus.RolledBack or
            SemanticGatewayAdmissionStatus.Released or
            SemanticGatewayAdmissionStatus.AdmissionNotFound or
            SemanticGatewayAdmissionStatus.AdmissionExpired or
            SemanticGatewayAdmissionStatus.GenerationExpired)
        {
            _admissions.TryRemove(claim.AdmissionId, out _);
        }
        if (!countSuccess)
        {
            if (result.Status ==
                SemanticGatewayAdmissionStatus.BindingMismatch)
            {
                Interlocked.Increment(ref _bindingRejections);
            }
            return;
        }

        switch (result.Status)
        {
            case SemanticGatewayAdmissionStatus.Committed:
                Interlocked.Increment(ref _admissionsCommitted);
                break;
            case SemanticGatewayAdmissionStatus.Refreshed:
                Interlocked.Increment(ref _admissionsRefreshed);
                break;
            case SemanticGatewayAdmissionStatus.RolledBack:
                Interlocked.Increment(ref _admissionsRolledBack);
                break;
            case SemanticGatewayAdmissionStatus.Released:
                Interlocked.Increment(ref _admissionsReleased);
                break;
            case SemanticGatewayAdmissionStatus.AdmissionExpired:
                Interlocked.Increment(ref _admissionsExpired);
                break;
            case SemanticGatewayAdmissionStatus.BindingMismatch:
                Interlocked.Increment(ref _bindingRejections);
                break;
            case SemanticGatewayAdmissionStatus.RouteRejected:
                Interlocked.Increment(ref _routeRejections);
                break;
        }
    }

    private void RecordAdmissionRejection(
        SemanticGatewayAdmissionStatus status)
    {
        if (status is
            SemanticGatewayAdmissionStatus.CapacityExceeded or
            SemanticGatewayAdmissionStatus.GenerationCapacityExceeded)
        {
            Interlocked.Increment(ref _capacityRejections);
        }
        else if (status is
            SemanticGatewayAdmissionStatus.PrincipalMismatch or
            SemanticGatewayAdmissionStatus.ConnectionConflict or
            SemanticGatewayAdmissionStatus.BindingMismatch)
        {
            Interlocked.Increment(ref _bindingRejections);
        }
    }

    private static SemanticGatewayAdmissionStatus AdmissionStatus(
        RedisResult result)
    {
        var status = checked((byte)
            RedisSemanticGatewayResultReader.Int64(result));
        if (!Enum.IsDefined(
                typeof(SemanticGatewayAdmissionStatus),
                status))
        {
            throw new InvalidDataException(
                "Redis returned an unknown admission status.");
        }

        return (SemanticGatewayAdmissionStatus)status;
    }

    private static SemanticGatewayRouteSelectionStatus RouteStatus(
        RedisResult result)
    {
        var status = checked((byte)
            RedisSemanticGatewayResultReader.Int64(result));
        if (!Enum.IsDefined(
                typeof(SemanticGatewayRouteSelectionStatus),
                status))
        {
            throw new InvalidDataException(
                "Redis returned an unknown route rejection status.");
        }

        return (SemanticGatewayRouteSelectionStatus)status;
    }

    private static void RequireAdmissionRequest(
        GatewayLoginGenerationId generationId,
        SemanticGatewayPrincipal principal,
        SemanticGatewayConnectionSource source,
        SemanticGatewayRouteTarget target)
    {
        if (!generationId.IsValid)
        {
            throw new ArgumentException(
                "A valid login-generation ID is required.",
                nameof(generationId));
        }
        RequirePrincipal(principal);
        RequireSource(source);
        if (!target.IsValid)
        {
            throw new ArgumentException(
                "A valid exact route target is required.",
                nameof(target));
        }
    }

    private static void RequireClaim(
        SemanticGatewayAdmissionClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!claim.AdmissionId.IsValid ||
            !claim.GenerationId.IsValid ||
            !claim.Principal.IsValid ||
            !claim.Source.IsValid ||
            !claim.Target.IsValid ||
            !claim.NodeId.IsValid ||
            claim.WorkerRevision <= 0)
        {
            throw new ArgumentException(
                "A complete semantic-gateway admission claim is required.",
            nameof(claim));
        }
    }

    private readonly record struct RedisSemanticGatewayRefreshResult(
        SemanticGatewayAdmissionResult AdmissionResult,
        DateTimeOffset GenerationExpiresAt);
}
