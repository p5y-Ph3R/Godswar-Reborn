using System.Security.Cryptography;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Sessions;
using StackExchange.Redis;

namespace Godswar.Server.Infrastructure.Redis;

internal sealed partial class RedisGameTicketStore
{
    public async ValueTask<SecureTicketIssueResult> IssueAsync(
        SecureLoginGeneration generation,
        SecureConnectionContext loginConnection,
        SecureGameTarget target,
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ValidateOperation(deadline, cancellationToken);
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(loginConnection);
        ArgumentNullException.ThrowIfNull(target);
        if (loginConnection.Role != SecureEndpointRole.Login)
        {
            throw new ArgumentException(
                "A ticket can be issued only from a secure login connection.",
                nameof(loginConnection));
        }
        SecureTicketModelValidation.ValidateAccount(
            generation.AccountId,
            generation.Username);
        ThrowIfDisposed();
        if (generation.AuthorityId != _authorityId)
        {
            return new SecureTicketIssueResult(
                SecureTicketIssueStatus.GenerationRejected,
                null);
        }

        using var lifetime =
            deadline.CreateCancellationSource(cancellationToken);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var issued = await TryIssueAsync(
                generation,
                loginConnection,
                target,
                deadline,
                lifetime.Token);
            if (issued.Status != RedisIssueAttemptStatus.Collision)
            {
                return issued.Result;
            }
        }

        throw new CryptographicException(
            "CSPRNG could not produce a unique ticket identity.");
    }

    private async ValueTask<RedisIssueAttempt> TryIssueAsync(
        SecureLoginGeneration generation,
        SecureConnectionContext loginConnection,
        SecureGameTarget target,
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken)
    {
        var grantIdBytes =
            new byte[SecureTicketModelValidation.GrantIdBytes];
        var ticketBytes =
            new byte[SecureTicketModelValidation.TicketBytes];
        var ticketHash = new byte[32];
        try
        {
            var grantId = CreateGrantId(grantIdBytes);
            FillNonzeroTicket(ticketBytes);
            SHA256.HashData(ticketBytes, ticketHash);

            var ticketKey = _keys.Ticket(ticketHash);
            var grantKey = _keys.TicketGrant(grantId);
            var result = await _executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Ticket,
                CoordinationDeadline.FromNow(
                    deadline.Timeout,
                    _timeProvider),
                database => database.ScriptEvaluateAsync(
                    RedisGameTicketScripts.Issue,
                    [
                        _keys.LoginAccount(generation.AccountId),
                        ticketKey,
                        grantKey,
                        _keys.TicketGenerationRegistry(),
                        _keys.OutstandingTicketRegistry()
                    ],
                    [
                        _authorityId.ToString("N"),
                        generation.GenerationId.ToString("N"),
                        generation.AccountId,
                        generation.Username,
                        grantId.ToString("N"),
                        Convert.ToHexString(ticketHash),
                        (int)loginConnection.ProtocolMajor,
                        (int)loginConnection.ProtocolMinor,
                        Convert.ToHexString(
                            loginConnection.ClientInstanceId.Span),
                        Convert.ToHexString(
                            loginConnection.OriginSha256.Span),
                        target.RouteHost,
                        target.TlsHost,
                        target.Audience,
                        (int)target.RoutePort,
                        (int)target.TlsPort,
                        target.ServerId,
                        (uint)target.Permissions,
                        TicketTtlMilliseconds(),
                        RetentionMilliseconds(),
                        _capacity
                    ]),
                cancellationToken);
            var response =
                RedisGameTicketResultReader.ReadIssue(result);
            UpdateSnapshot(response.Counts);
            return response.Status switch
            {
                1 => Issued(
                    generation,
                    target,
                    response.ExpiryUnixMilliseconds,
                    grantIdBytes,
                    ticketBytes,
                    grantId),
                0 => Rejected(
                    RedisIssueAttemptStatus.Complete,
                    SecureTicketIssueStatus.GenerationRejected),
                -1 => Rejected(
                    RedisIssueAttemptStatus.Complete,
                    SecureTicketIssueStatus.CapacityExceeded),
                -2 => new RedisIssueAttempt(
                    RedisIssueAttemptStatus.Collision,
                    default),
                _ => throw new InvalidDataException(
                    "Redis returned an invalid ticket-issue status.")
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(grantIdBytes);
            CryptographicOperations.ZeroMemory(ticketBytes);
            CryptographicOperations.ZeroMemory(ticketHash);
        }
    }

    private RedisIssueAttempt Issued(
        SecureLoginGeneration generation,
        SecureGameTarget target,
        long expiryUnixMilliseconds,
        ReadOnlySpan<byte> grantId,
        ReadOnlySpan<byte> ticket,
        Guid grantIdValue)
    {
        SecureGameGrant? grant = null;
        try
        {
            grant = new SecureGameGrant(
                target.RouteHost,
                target.TlsHost,
                target.Audience,
                target.RoutePort,
                target.TlsPort,
                target.ServerId,
                checked((ulong)expiryUnixMilliseconds),
                grantId,
                ticket);
            var lease = new SecureGameGrantLease(
                this,
                generation.GenerationId,
                grantIdValue,
                grant);
            grant = null;
            return new RedisIssueAttempt(
                RedisIssueAttemptStatus.Complete,
                new SecureTicketIssueResult(
                    SecureTicketIssueStatus.Issued,
                    lease));
        }
        finally
        {
            grant?.Dispose();
        }
    }

    private static RedisIssueAttempt Rejected(
        RedisIssueAttemptStatus status,
        SecureTicketIssueStatus issueStatus) =>
        new(
            status,
            new SecureTicketIssueResult(issueStatus, null));

    private static Guid CreateGrantId(Span<byte> destination)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            RandomNumberGenerator.Fill(destination);
            var grantId = new Guid(destination);
            if (grantId != Guid.Empty)
            {
                return grantId;
            }
        }

        throw new CryptographicException(
            "CSPRNG returned repeated invalid grant IDs.");
    }

    private static void FillNonzeroTicket(Span<byte> destination)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            RandomNumberGenerator.Fill(destination);
            if (!SecureTicketModelValidation.IsAllZero(destination))
            {
                return;
            }
        }

        throw new CryptographicException(
            "CSPRNG returned repeated invalid ticket secrets.");
    }

    private enum RedisIssueAttemptStatus : byte
    {
        Complete = 1,
        Collision = 2
    }

    private readonly record struct RedisIssueAttempt(
        RedisIssueAttemptStatus Status,
        SecureTicketIssueResult Result);
}
