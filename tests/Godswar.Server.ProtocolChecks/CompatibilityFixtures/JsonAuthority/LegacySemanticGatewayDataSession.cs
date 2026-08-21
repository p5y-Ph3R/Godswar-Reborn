using Godswar.Server.Application.Gateway;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Security.Authentication;

namespace Godswar.Server.State;

/// <summary>
/// JSON-only local-development compatibility session. Despite the retained
/// filename for migration history, this implementation uses only focused
/// account and route contracts at the gateway boundary.
/// </summary>
internal sealed class JsonSemanticGatewayDataSession :
    ISemanticGatewayDataSession
{
    private readonly AccountAuthenticationService _authentication;
    private readonly ISemanticGatewayCharacterRouteReader _routes;
    private IAsyncDisposable? _owner;

    private JsonSemanticGatewayDataSession(
        IAsyncDisposable owner,
        AccountAuthenticationService authentication,
        ISemanticGatewayCharacterRouteReader routes)
    {
        _owner = owner;
        _authentication = authentication;
        _routes = routes;
    }

    public static async ValueTask<ISemanticGatewayDataSession> OpenAsync(
        ServerOptions options,
        string dataPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        var json = new JsonGameStore(dataPath);
        try
        {
            await json.EnsureSeedDataAsync(cancellationToken);
            return new JsonSemanticGatewayDataSession(
                json,
                new AccountAuthenticationService(
                    json,
                    json,
                    options.Authentication),
                json);
        }
        catch
        {
            await json.DisposeAsync();
            throw;
        }
    }

    public async Task<SemanticGatewayAuthenticatedAccount?>
        AuthenticateAsync(
            string username,
            ReadOnlyMemory<byte> password,
            CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var result = await _authentication.AuthenticateAsync(
            username,
            password,
            cancellationToken);
        return result.IsAccepted && result.Account is not null
            ? new SemanticGatewayAuthenticatedAccount(
                result.Account.Id,
                result.Account.Username)
            : null;
    }

    public Task<SemanticGatewayCharacterRoute?> FindCharacterRouteAsync(
        int accountId,
        RealmId realmId,
        CancellationToken cancellationToken = default) =>
        FindCharacterRouteCoreAsync(
            accountId,
            realmId,
            cancellationToken);

    public Task<RealmCatalogSnapshot> ReadEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RealmCatalogSnapshot(
        [
            new RealmCatalogEntry(
                RealmId.Tempest,
                "Tempest",
                "KAL3jcIzqGgKvOf1dbYZKC8cS",
                "127.1.1.110",
                gamePort: 7_000,
                serverLimit: 250,
                recommended: true,
                displayOrder: 1)
        ]));
    }

    public async ValueTask DisposeAsync()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null)
        {
            return;
        }

        try
        {
            await _authentication.DisposeAsync();
        }
        finally
        {
            await owner.DisposeAsync();
        }
    }

    private Task<SemanticGatewayCharacterRoute?>
        FindCharacterRouteCoreAsync(
            int accountId,
            RealmId realmId,
            CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _routes.FindCharacterRouteAsync(
            accountId,
            realmId,
            cancellationToken);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _owner) is null,
            this);
}
