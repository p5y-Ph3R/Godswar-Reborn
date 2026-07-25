namespace Godswar.Server.Networking.Secure;

internal interface IGameTicketStore : IDisposable
{
    SecureLoginGenerationResult BeginLogin(
        int accountId,
        string username);

    SecureTicketIssueResult Issue(
        SecureLoginGeneration generation,
        SecureConnectionContext loginConnection,
        SecureGameTarget target);

    SecureTicketConsumeResult Consume(
        SecureGameBind bind,
        SecureConnectionContext gameConnection,
        SecureGameTarget expectedTarget);

    void RevokeGeneration(SecureLoginGeneration generation);

    SecureGameTicketStoreSnapshot GetSnapshot();
}
