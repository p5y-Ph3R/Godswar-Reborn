using System.Security.Cryptography;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Gateway;
using Godswar.Server.Security.Authentication;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresAccountStoreIntegrationChecks
{
    private static async Task AssertSemanticGatewaySessionAsync(
        string connectionString,
        AccountIdentity routedAccount,
        AccountIdentity noRouteAccount,
        int expectedCharacterId)
    {
        var options = new ServerOptions
        {
            Storage = new StorageOptions
            {
                Provider = "Postgres",
                PostgresConnectionString = connectionString
            },
            Authentication = new AuthenticationOptions
            {
                Iterations = 100_000,
                MinimumStoredIterations = 100_000,
                MaximumStoredIterations = 200_000,
                MaximumConcurrentKdfs = 2,
                QueueCapacity = 8,
                QueueCredentialBytes = 256,
                QueueAdmissionTimeoutMilliseconds = 250,
                OperationTimeoutMilliseconds = 5_000,
                AllowPlaintextMigration = true
            }
        };
        var password = "legacy-local-only"u8.ToArray();
        var wrongPassword = "wrong-password"u8.ToArray();
        ISemanticGatewayDataSession? session = null;
        try
        {
            session = await PostgresSemanticGatewayDataSession.OpenAsync(
                options);
            var accepted = await session.AuthenticateAsync(
                routedAccount.Username,
                password);
            Check.True(
                accepted is not null,
                "PostgreSQL semantic gateway accepts a valid credential");
            Check.Equal(
                routedAccount.Id,
                accepted!.AccountId,
                "PostgreSQL semantic gateway preserves authenticated identity");

            Check.True(
                await session.AuthenticateAsync(
                    routedAccount.Username,
                    wrongPassword) is null,
                "PostgreSQL semantic gateway rejects an invalid credential");

            var routed = await session.FindCharacterRouteAsync(
                accepted.AccountId,
                RealmId.Tempest);
            Check.True(
                routed is not null,
                "PostgreSQL semantic gateway resolves an authenticated route");
            Check.Equal(
                expectedCharacterId,
                routed!.CharacterId,
                "PostgreSQL semantic gateway route preserves character identity");
            Check.Equal(
                MapId.FromLegacy(5),
                routed.MapId,
                "PostgreSQL semantic gateway route preserves map identity");

            var noRouteAccepted = await session.AuthenticateAsync(
                noRouteAccount.Username,
                password);
            Check.True(
                noRouteAccepted is not null,
                "PostgreSQL semantic gateway authenticates a no-route account");
            Check.True(
                await session.FindCharacterRouteAsync(
                    noRouteAccepted!.AccountId,
                    RealmId.Tempest) is null,
                "PostgreSQL semantic gateway returns no route when none exists");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(wrongPassword);
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }

        if (session is null)
        {
            throw new InvalidOperationException(
                "PostgreSQL semantic gateway session was not created.");
        }

        await session.DisposeAsync();
        var disposedPassword = "unused"u8.ToArray();
        try
        {
            await AssertDisposedAsync(
                async () =>
                {
                    _ = await session.AuthenticateAsync(
                        routedAccount.Username,
                        disposedPassword);
                },
                "disposed semantic gateway rejects authentication");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(disposedPassword);
        }
        await AssertDisposedAsync(
            async () =>
            {
                _ = await session.FindCharacterRouteAsync(
                    routedAccount.Id,
                    RealmId.Tempest);
            },
            "disposed semantic gateway rejects route lookup");
    }

    private static async Task AssertDisposedAsync(
        Func<Task> action,
        string description)
    {
        var rejected = false;
        try
        {
            await action();
        }
        catch (ObjectDisposedException)
        {
            rejected = true;
        }

        Check.True(rejected, description);
    }
}
