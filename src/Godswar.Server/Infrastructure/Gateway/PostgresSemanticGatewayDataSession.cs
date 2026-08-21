using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Accounts;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Realms;
using Godswar.Server.Security.Authentication;
using Npgsql;

namespace Godswar.Server.Infrastructure.Gateway;

internal sealed class PostgresSemanticGatewayDataSession :
    ISemanticGatewayDataSession
{
    private readonly AccountAuthenticationService _authentication;
    private readonly IRealmCatalogReader _realms;
    private readonly ISemanticGatewayCharacterRouteReader _routes;
    private NpgsqlDataSource? _dataSource;

    private PostgresSemanticGatewayDataSession(
        NpgsqlDataSource dataSource,
        AccountAuthenticationService authentication,
        IRealmCatalogReader realms,
        ISemanticGatewayCharacterRouteReader routes)
    {
        _dataSource = dataSource;
        _authentication = authentication;
        _realms = realms;
        _routes = routes;
    }

    public static async ValueTask<ISemanticGatewayDataSession> OpenAsync(
        ServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var dataSource = NpgsqlDataSource.Create(
            options.Storage.PostgresConnectionString);
        try
        {
            await PostgresSchemaStartup.InitializeAsync(
                dataSource,
                cancellationToken);
            var accounts = new PostgresAccountStore(dataSource);
            return new PostgresSemanticGatewayDataSession(
                dataSource,
                new AccountAuthenticationService(
                    accounts,
                    accounts,
                    options.Authentication),
                new PostgresRealmCatalogReader(dataSource),
                new PostgresSemanticGatewayCharacterRouteReader(
                    dataSource));
        }
        catch
        {
            await dataSource.DisposeAsync();
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
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _routes.FindCharacterRouteAsync(
            accountId,
            realmId,
            cancellationToken);
    }

    public Task<RealmCatalogSnapshot> ReadEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _realms.ReadEnabledAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        var dataSource = Interlocked.Exchange(ref _dataSource, null);
        if (dataSource is null)
        {
            return;
        }

        try
        {
            await _authentication.DisposeAsync();
        }
        finally
        {
            await dataSource.DisposeAsync();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _dataSource) is null,
            this);
}
