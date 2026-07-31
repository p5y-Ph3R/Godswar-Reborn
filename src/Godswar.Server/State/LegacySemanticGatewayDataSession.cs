using Godswar.Server.Application.Gateway;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Security.Authentication;

namespace Godswar.Server.State;

/// <summary>
/// Transitional adapter from the focused gateway application contract to
/// the existing JSON/PostgreSQL stores. Only this persistence-owned type
/// knows about the broad legacy store.
/// </summary>
internal sealed class LegacySemanticGatewayDataSession :
    ISemanticGatewayDataSession
{
    private readonly AccountAuthenticationService _authentication;
    private IGameStore? _store;

    private LegacySemanticGatewayDataSession(
        IGameStore store,
        AccountAuthenticationService authentication)
    {
        _store = store;
        _authentication = authentication;
    }

    public static async ValueTask<ISemanticGatewayDataSession> OpenAsync(
        ServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var profile = ServerRuntimeProfilePolicy.Validate(options);
        IGameStore store = profile.StorageProvider switch
        {
            GameStorageProviderKind.Postgres =>
                new PostgresGameStore(
                    options.Storage.PostgresConnectionString),
            GameStorageProviderKind.Json =>
                new JsonGameStore(options.DataPath),
            _ => throw new InvalidDataException(
                "The gateway storage provider is unsupported.")
        };

        try
        {
            await store.EnsureSeedDataAsync(cancellationToken);
            var authentication = new AccountAuthenticationService(
                store,
                options.Authentication);
            return new LegacySemanticGatewayDataSession(
                store,
                authentication);
        }
        catch
        {
            await store.DisposeAsync();
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

    public async Task<SemanticGatewayCharacterRoute?>
        FindCharacterRouteAsync(
            int accountId,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        var store = _store ??
            throw new ObjectDisposedException(
                nameof(LegacySemanticGatewayDataSession));
        var character = await store.GetFirstCharacterAsync(
            accountId,
            cancellationToken);
        return character is null
            ? null
            : new SemanticGatewayCharacterRoute(
                character.Id,
                MapId.FromLegacy(character.CurrentMap));
    }

    public async ValueTask DisposeAsync()
    {
        var store = Interlocked.Exchange(ref _store, null);
        if (store is null)
        {
            return;
        }

        try
        {
            await _authentication.DisposeAsync();
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _store) is null,
            this);
}
