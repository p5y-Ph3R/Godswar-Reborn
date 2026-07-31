using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.Gateway;

/// <summary>
/// Server-derived account identity returned only after credential
/// verification. Transport code never receives a persistence model.
/// </summary>
internal sealed record SemanticGatewayAuthenticatedAccount(
    int AccountId,
    string Username);

/// <summary>
/// Minimal durable character projection needed for initial worker routing.
/// </summary>
internal sealed record SemanticGatewayCharacterRoute(
    int CharacterId,
    MapId MapId);

/// <summary>
/// Minimal durable projection used to route an authenticated account. The
/// reader fails closed when more than one active character exists.
/// </summary>
internal interface ISemanticGatewayCharacterRouteReader
{
    Task<SemanticGatewayCharacterRoute?> FindCharacterRouteAsync(
        int accountId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Focused application boundary used by the semantic gateway. Persistence
/// providers remain behind the infrastructure composition session.
/// </summary>
internal interface ISemanticGatewayDataSession : IAsyncDisposable
{
    Task<SemanticGatewayAuthenticatedAccount?> AuthenticateAsync(
        string username,
        ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken = default);

    Task<SemanticGatewayCharacterRoute?> FindCharacterRouteAsync(
        int accountId,
        CancellationToken cancellationToken = default);
}
