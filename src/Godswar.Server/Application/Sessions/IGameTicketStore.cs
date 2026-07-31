namespace Godswar.Server.Application.Sessions;

internal interface IGameTicketStore : IAsyncDisposable
{
    ValueTask<SecureLoginGenerationResult> BeginLoginAsync(
        int accountId,
        string username,
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken = default);

    ValueTask<SecureTicketIssueResult> IssueAsync(
        SecureLoginGeneration generation,
        SecureConnectionContext loginConnection,
        SecureGameTarget target,
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken = default);

    ValueTask<SecureTicketConsumeResult> ConsumeAsync(
        SecureGameBind bind,
        SecureConnectionContext gameConnection,
        SecureGameTarget expectedTarget,
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken = default);

    ValueTask RevokeGenerationAsync(
        SecureLoginGeneration generation,
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken = default);
}

internal interface IGameTicketStoreSnapshotSource
{
    SecureGameTicketStoreSnapshot GetCachedSnapshot();
}

internal interface ISecureGameGrantLeaseAuthority
{
    ValueTask<bool> TryActivateGrantAsync(
        Guid generationId,
        Guid grantId,
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken);

    ValueTask RevokeGrantAsync(
        Guid generationId,
        Guid grantId,
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken);
}
