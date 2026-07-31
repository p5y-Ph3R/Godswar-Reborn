using System.Security.Cryptography;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Sessions;
using StackExchange.Redis;

namespace Godswar.Server.Infrastructure.Redis;

internal sealed partial class RedisGameTicketStore
{
    public async ValueTask<SecureTicketConsumeResult> ConsumeAsync(
        SecureGameBind bind,
        SecureConnectionContext gameConnection,
        SecureGameTarget expectedTarget,
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ValidateOperation(deadline, cancellationToken);
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(gameConnection);
        ArgumentNullException.ThrowIfNull(expectedTarget);
        if (gameConnection.Role != SecureEndpointRole.Game)
        {
            throw new ArgumentException(
                "A ticket can be consumed only on a secure game connection.",
                nameof(gameConnection));
        }
        ThrowIfDisposed();

        var grantIdBytes =
            new byte[SecureTicketModelValidation.GrantIdBytes];
        var ticketBytes =
            new byte[SecureTicketModelValidation.TicketBytes];
        var suppliedHash = new byte[32];
        try
        {
            if (!bind.TryCopySecrets(grantIdBytes, ticketBytes))
            {
                return Rejected(SecureTicketConsumeStatus.Rejected);
            }
            SHA256.HashData(ticketBytes, suppliedHash);
            var grantId = new Guid(grantIdBytes);

            using var lifetime =
                deadline.CreateCancellationSource(cancellationToken);
            var result = await _executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Ticket,
                CoordinationDeadline.FromNow(
                    deadline.Timeout,
                    _timeProvider),
                database => database.ScriptEvaluateAsync(
                    RedisGameTicketScripts.Consume,
                    [
                        _keys.TicketGrant(grantId),
                        _keys.TicketGenerationRegistry(),
                        _keys.OutstandingTicketRegistry()
                    ],
                    [
                        Convert.ToHexString(suppliedHash),
                        grantId.ToString("N"),
                        (int)gameConnection.ProtocolMajor,
                        (int)gameConnection.ProtocolMinor,
                        Convert.ToHexString(
                            gameConnection.ClientInstanceId.Span),
                        Convert.ToHexString(
                            gameConnection.OriginSha256.Span),
                        expectedTarget.RouteHost,
                        expectedTarget.TlsHost,
                        expectedTarget.Audience,
                        (int)expectedTarget.RoutePort,
                        (int)expectedTarget.TlsPort,
                        expectedTarget.ServerId,
                        (uint)expectedTarget.Permissions
                    ]),
                lifetime.Token);
            var response =
                RedisGameTicketResultReader.ReadConsume(result);
            UpdateSnapshot(response.Counts);
            return response.Status ==
                    SecureTicketConsumeStatus.Accepted
                ? new SecureTicketConsumeResult(
                    response.Status,
                    new SecureBoundGamePrincipal(
                        response.AccountId,
                        response.Username,
                        response.Permissions,
                        response.GenerationId))
                : Rejected(response.Status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(grantIdBytes);
            CryptographicOperations.ZeroMemory(ticketBytes);
            CryptographicOperations.ZeroMemory(suppliedHash);
        }
    }

    private static SecureTicketConsumeResult Rejected(
        SecureTicketConsumeStatus status) =>
        new(status, null);
}
