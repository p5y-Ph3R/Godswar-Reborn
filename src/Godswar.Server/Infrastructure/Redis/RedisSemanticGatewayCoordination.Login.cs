using System.Net;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Gateway;
using StackExchange.Redis;

namespace Godswar.Server.Infrastructure.Redis;

internal sealed partial class RedisSemanticGatewayCoordination
{
    public async ValueTask<SemanticGatewayLoginResult> StartLoginAsync(
        SemanticGatewayPrincipal principal,
        SemanticGatewayConnectionSource loginSource,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken)
    {
        RequirePrincipal(principal);
        RequireSource(loginSource);
        EnsureAvailable(deadline, cancellationToken);
        var generationId = GatewayLoginGenerationId.New();
        var username = principal.CanonicalUsername!;
        var accountKey = _keys.LoginAccount(principal.AccountId);
        var nameKey = _keys.LoginName(username);
        var connectionKey = _keys.LoginConnection(
            loginSource.ConnectionId.Value);
        var result = await _executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            deadline,
            database => database.ScriptEvaluateAsync(
                RedisSemanticGatewayScripts.StartLogin,
                [
                    accountKey,
                    nameKey,
                    connectionKey,
                    _keys.GatewayCounters(),
                    _keys.GatewayExpiry()
                ],
                [
                    generationId.ToString(),
                    principal.AccountId,
                    username,
                    loginSource.ConnectionId.ToString(),
                    loginSource.Address!.ToString(),
                    TtlMilliseconds(_limits.LoginGenerationTtl),
                    TtlMilliseconds(StateStorageTtl),
                    _limits.MaximumLoginGenerations
                ]),
            cancellationToken);
        var values =
            RedisSemanticGatewayResultReader.Array(result, 5);
        var status = LoginStatus(values[0]);
        if (status != SemanticGatewayLoginStatus.Started)
        {
            RecordLoginRejection(status);
            return new(status, null, 0);
        }

        var returnedId = new GatewayLoginGenerationId(
            RedisSemanticGatewayResultReader.Guid(values[1]));
        if (returnedId != generationId)
        {
            throw new InvalidDataException(
                "Redis returned a different login-generation ID.");
        }
        var lease = new SemanticGatewayLoginGenerationLease(
            returnedId,
            RedisSemanticGatewayResultReader.Int64(values[2]),
            principal,
            loginSource,
            RedisSemanticGatewayResultReader.Timestamp(values[4]));
        var invalidated =
            RedisSemanticGatewayResultReader.Int32(values[3]);
        if (invalidated is < 0 or > 1)
        {
            throw new InvalidDataException(
                "Redis returned an invalid admission invalidation count.");
        }

        var hadPrevious = _generations.ContainsKey(principal.AccountId);
        RemoveObservedAccountAdmissions(principal.AccountId);
        CacheGeneration(lease);
        Interlocked.Increment(ref _loginGenerationsStarted);
        if (hadPrevious)
        {
            Interlocked.Increment(ref _loginGenerationsSuperseded);
        }
        if (invalidated != 0)
        {
            Interlocked.Add(ref _admissionsInvalidated, invalidated);
        }
        return new(status, lease, invalidated);
    }

    public async ValueTask<bool> ActivateLoginAsync(
        SemanticGatewayLoginGenerationLease generation,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken)
    {
        RequireGeneration(generation);
        EnsureAvailable(deadline, cancellationToken);
        var result = await _executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            deadline,
            database => database.ScriptEvaluateAsync(
                RedisSemanticGatewayScripts.ActivateLogin,
                [
                    _keys.LoginAccount(
                        generation.Principal.AccountId),
                    _keys.LoginName(
                        generation.Principal.CanonicalUsername!)
                ],
                GenerationArguments(generation)),
            cancellationToken);
        var activated =
            RedisSemanticGatewayResultReader.Int64(result) == 1;
        if (activated)
        {
            CacheGeneration(generation);
        }
        return activated;
    }

    public async ValueTask<bool> CancelLoginAsync(
        SemanticGatewayLoginGenerationLease generation,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken)
    {
        RequireGeneration(generation);
        EnsureAvailable(deadline, cancellationToken);
        var result = await _executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            deadline,
            database => database.ScriptEvaluateAsync(
                RedisSemanticGatewayScripts.CancelLogin,
                [
                    _keys.LoginAccount(
                        generation.Principal.AccountId),
                    _keys.LoginName(
                        generation.Principal.CanonicalUsername!),
                    _keys.LoginConnection(
                        generation.LoginSource.ConnectionId.Value),
                    _keys.GatewayCounters(),
                    _keys.GatewayExpiry()
                ],
                GenerationArguments(generation)),
            cancellationToken);
        var values =
            RedisSemanticGatewayResultReader.Array(result, 2);
        var cancelled =
            RedisSemanticGatewayResultReader.Int64(values[0]) == 1;
        var invalidated =
            RedisSemanticGatewayResultReader.Int32(values[1]);
        if (cancelled)
        {
            RemoveObservedGeneration(
                generation.GenerationId,
                generation.Principal.AccountId);
            if (invalidated != 0)
            {
                Interlocked.Add(
                    ref _admissionsInvalidated,
                    invalidated);
            }
        }
        return cancelled;
    }

    public async ValueTask<SemanticGatewayLoginLookupResult>
        FindActivatedLoginAsync(
            string canonicalUsername,
            IPAddress observedGameAddress,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        ValidateCanonicalUsername(canonicalUsername);
        var address = SemanticGatewayConnectionSource.Normalize(
            observedGameAddress ??
            throw new ArgumentNullException(nameof(observedGameAddress)));
        EnsureAvailable(deadline, cancellationToken);
        var result = await _executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            deadline,
            database => database.ScriptEvaluateAsync(
                RedisSemanticGatewayScripts.FindActivatedLogin,
                [_keys.LoginName(canonicalUsername)],
                [address.ToString()]),
            cancellationToken);
        var values =
            RedisSemanticGatewayResultReader.Array(result, 8);
        var status = LoginLookupStatus(values[0]);
        if (status != SemanticGatewayLoginLookupStatus.Found)
        {
            if (status ==
                SemanticGatewayLoginLookupStatus.SourceAddressMismatch)
            {
                Interlocked.Increment(ref _bindingRejections);
            }
            return new(status, null);
        }

        var principal = new SemanticGatewayPrincipal(
            RedisSemanticGatewayResultReader.Int32(values[3]),
            RedisSemanticGatewayResultReader.Text(
                values[4],
                SemanticGatewayPrincipal.MaximumUsernameLength));
        var source = new SemanticGatewayConnectionSource(
            new GatewayConnectionId(
                RedisSemanticGatewayResultReader.Guid(values[5])),
            IPAddress.Parse(
                RedisSemanticGatewayResultReader.Text(values[6], 45)));
        var generation =
            new SemanticGatewayLoginGenerationLease(
                new GatewayLoginGenerationId(
                    RedisSemanticGatewayResultReader.Guid(values[1])),
                RedisSemanticGatewayResultReader.Int64(values[2]),
                principal,
                source,
                RedisSemanticGatewayResultReader.Timestamp(values[7]));
        CacheGeneration(generation);
        return new(status, generation);
    }

    private static RedisValue[] GenerationArguments(
        SemanticGatewayLoginGenerationLease generation) =>
        [
            generation.GenerationId.ToString(),
            generation.Sequence,
            generation.Principal.AccountId,
            generation.Principal.CanonicalUsername!,
            generation.LoginSource.ConnectionId.ToString(),
            generation.LoginSource.Address!.ToString()
        ];

    private void RemoveObservedAccountAdmissions(int accountId)
    {
        foreach (var admission in _admissions)
        {
            if (admission.Value.Principal.AccountId == accountId)
            {
                _admissions.TryRemove(admission);
            }
        }
    }

    private void RecordLoginRejection(
        SemanticGatewayLoginStatus status)
    {
        switch (status)
        {
            case SemanticGatewayLoginStatus.IdentityConflict:
                Interlocked.Increment(ref _identityConflicts);
                break;
            case SemanticGatewayLoginStatus.ConnectionConflict:
                Interlocked.Increment(ref _bindingRejections);
                break;
            case SemanticGatewayLoginStatus.CapacityExceeded:
                Interlocked.Increment(ref _capacityRejections);
                break;
        }
    }

    private static SemanticGatewayLoginStatus LoginStatus(
        RedisResult result)
    {
        var status = checked((byte)
            RedisSemanticGatewayResultReader.Int64(result));
        if (!Enum.IsDefined(
                typeof(SemanticGatewayLoginStatus),
                status))
        {
            throw new InvalidDataException(
                "Redis returned an unknown login status.");
        }

        return (SemanticGatewayLoginStatus)status;
    }

    private static SemanticGatewayLoginLookupStatus LoginLookupStatus(
        RedisResult result)
    {
        var status = checked((byte)
            RedisSemanticGatewayResultReader.Int64(result));
        if (!Enum.IsDefined(
                typeof(SemanticGatewayLoginLookupStatus),
                status))
        {
            throw new InvalidDataException(
                "Redis returned an unknown login-lookup status.");
        }

        return (SemanticGatewayLoginLookupStatus)status;
    }

    private static void RequireGeneration(
        SemanticGatewayLoginGenerationLease generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (!generation.GenerationId.IsValid ||
            generation.Sequence <= 0 ||
            !generation.Principal.IsValid ||
            !generation.LoginSource.IsValid)
        {
            throw new ArgumentException(
                "A valid login-generation lease is required.",
                nameof(generation));
        }
    }

    private static void RequirePrincipal(
        SemanticGatewayPrincipal principal)
    {
        if (!principal.IsValid)
        {
            throw new ArgumentException(
                "A valid semantic-gateway principal is required.",
                nameof(principal));
        }
    }

    private static void RequireSource(
        SemanticGatewayConnectionSource source)
    {
        if (!source.IsValid)
        {
            throw new ArgumentException(
                "A valid semantic-gateway source is required.",
                nameof(source));
        }
    }

    private static void ValidateCanonicalUsername(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        if (username.Length is < 1 or >
                SemanticGatewayPrincipal.MaximumUsernameLength ||
            username.Any(static value => value is < '!' or > '~'))
        {
            throw new ArgumentException(
                "Canonical username must be exact printable ASCII.",
                nameof(username));
        }
    }
}
