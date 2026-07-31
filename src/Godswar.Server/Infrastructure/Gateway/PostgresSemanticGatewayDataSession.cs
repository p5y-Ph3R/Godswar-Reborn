using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Infrastructure.Accounts;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Gateway;

internal sealed class PostgresSemanticGatewayDataSession :
    ISemanticGatewayDataSession
{
    private readonly AccountAuthenticationService _authentication;
    private readonly ISemanticGatewayCharacterRouteReader _routes;
    private NpgsqlDataSource? _dataSource;

    private PostgresSemanticGatewayDataSession(
        NpgsqlDataSource dataSource,
        AccountAuthenticationService authentication,
        ISemanticGatewayCharacterRouteReader routes)
    {
        _dataSource = dataSource;
        _authentication = authentication;
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
            await new PostgresSchemaMigrationRunner(dataSource)
                .InitializeGodswarSchemaAsync(cancellationToken);
            var accounts = new PostgresAccountStore(dataSource);
            return new PostgresSemanticGatewayDataSession(
                dataSource,
                new AccountAuthenticationService(
                    accounts,
                    accounts,
                    options.Authentication),
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
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _routes.FindCharacterRouteAsync(
            accountId,
            cancellationToken);
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
