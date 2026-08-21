using System.Security.Cryptography;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Accounts;
using Godswar.Server.Infrastructure.Gateway;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresAccountStoreIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL focused account persistence adapter";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string LegacyPassword = "legacy-local-only";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(
                ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var migrationRunner =
            new PostgresSchemaMigrationRunner(dataSource);
        await migrationRunner.InitializeGodswarSchemaAsync();

        var store = new PostgresAccountStore(dataSource);
        var token = Guid.NewGuid().ToString("N")[..10];
        var versionedUsername = $"B20B_{token}";
        var caseVariantUsername = versionedUsername.ToLowerInvariant();
        var legacyUsername = $"Legacy_{token}";
        var gatewayNoRouteUsername = $"NoRoute_{token}";
        var concurrentUsername = $"Race_{token}";
        var plaintextVerifierUsername = $"Plain_{token}";
        var malformedVerifierUsername = $"BadHash_{token}";
        var overlongLegacyCandidate =
            $"Invalid_{token}_{new string('X', AccountIdentity.MaximumUsernameLength)}";
        var nonAsciiLegacyCandidate = $"Invalid_{token}_\u007F";
        var invalidLegacyUsernames = new[]
        {
            $" \t{overlongLegacyCandidate}\r\n",
            $" {nonAsciiLegacyCandidate} "
        };
        var invalidDurableCandidates = new[]
        {
            overlongLegacyCandidate,
            nonAsciiLegacyCandidate
        };
        var fixtureUsernames = new[]
        {
            versionedUsername,
            caseVariantUsername,
            legacyUsername,
            gatewayNoRouteUsername,
            concurrentUsername,
            plaintextVerifierUsername,
            malformedVerifierUsername,
            overlongLegacyCandidate,
            nonAsciiLegacyCandidate
        };

        try
        {
            await AssertInvalidLegacyUsernamesCannotMutateAsync(
                dataSource,
                store,
                invalidLegacyUsernames,
                invalidDurableCandidates);
            await AssertCredentialLifecycleAsync(
                dataSource,
                store,
                versionedUsername,
                caseVariantUsername);
            await AssertInvalidVerifierInputsAsync(
                dataSource,
                store,
                plaintextVerifierUsername,
                malformedVerifierUsername);
            await AssertConcurrentCredentialWritesAsync(
                dataSource,
                store,
                concurrentUsername);
            var legacyAccount = await AssertLegacyCompatibilityAsync(
                dataSource,
                store,
                legacyUsername);
            var noRouteAccount =
                await store.LoginOrCreateLegacyAccountAsync(
                    gatewayNoRouteUsername,
                    LegacyPassword);
            var characterId = await AssertSemanticGatewayRouteAsync(
                dataSource,
                legacyAccount.Id,
                token);
            await AssertSemanticGatewaySessionAsync(
                connectionString,
                legacyAccount,
                noRouteAccount,
                characterId);
        }
        finally
        {
            await DeleteFixturesAsync(dataSource, fixtureUsernames);
        }
    }

    private static async Task
        AssertInvalidLegacyUsernamesCannotMutateAsync(
            NpgsqlDataSource dataSource,
            PostgresAccountStore store,
            IReadOnlyList<string> invalidUsernames,
            IReadOnlyList<string> durableCandidates)
    {
        for (var index = 0; index < invalidUsernames.Count; index++)
        {
            var durableCandidate = durableCandidates[index];
            var before = await ReadAccountCountAsync(
                dataSource,
                durableCandidate);
            Check.Equal(
                0L,
                before,
                "invalid legacy username fixture starts absent");

            var rejected = false;
            try
            {
                _ = await store.LoginOrCreateLegacyAccountAsync(
                    invalidUsernames[index],
                    "must-not-persist");
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            Check.True(
                rejected,
                "invalid post-trim PostgreSQL legacy username is rejected");
            Check.Equal(
                before,
                await ReadAccountCountAsync(
                    dataSource,
                    durableCandidate),
                "invalid legacy username creates no PostgreSQL account row");
        }
    }

    private static async Task AssertCredentialLifecycleAsync(
        NpgsqlDataSource dataSource,
        PostgresAccountStore store,
        string username,
        string caseVariantUsername)
    {
        var firstVerifier = CreateStructuralVerifier(100_000, 0x41);
        var replacementVerifier =
            CreateStructuralVerifier(100_000, 0x42);
        var wrongExpectedVerifier =
            CreateStructuralVerifier(100_000, 0x43);

        var created = await store.TryCreateAccountWithCredentialAsync(
            username,
            firstVerifier);
        Check.True(
            created is not null,
            "focused account adapter creates a versioned credential");
        Check.Equal(
            username,
            created!.Username,
            "account creation preserves username casing");
        Check.True(
            await store.TryCreateAccountWithCredentialAsync(
                username,
                firstVerifier) is null,
            "duplicate account creation reports a conflict");
        Check.True(
            await store.FindAccountByUsernameAsync(caseVariantUsername)
                is null,
            "account lookup does not normalize username casing");

        var caseVariant =
            await store.TryCreateAccountWithCredentialAsync(
                caseVariantUsername,
                firstVerifier);
        Check.True(
            caseVariant is not null &&
            caseVariant.Id != created.Id,
            "case-distinct usernames retain independent identities");

        var byId = await store.FindAccountByIdAsync(created.Id);
        var byName = await store.FindAccountByUsernameAsync(username);
        var credential =
            await store.FindAccountCredentialAsync(username);
        Check.True(
            byId is not null && byName is not null &&
            credential is not null,
            "created account is available through every focused reader");
        Check.Equal(
            created,
            byId!,
            "account directory resolves the created identity by id");
        Check.Equal(
            created,
            byName!,
            "account directory resolves the created identity by name");
        Check.Equal(
            firstVerifier,
            credential!.Verifier,
            "credential lookup returns the stored versioned verifier");

        Check.True(
            !await store.TryReplaceAccountCredentialAsync(
                created.Id,
                wrongExpectedVerifier,
                replacementVerifier),
            "credential replacement rejects a stale expected verifier");
        Check.True(
            await store.TryReplaceAccountCredentialAsync(
                created.Id,
                firstVerifier,
                replacementVerifier),
            "credential replacement uses compare-and-swap semantics");
        var replacedCredential =
            await store.FindAccountCredentialAsync(username);
        Check.True(
            replacedCredential is not null,
            "replaced credential remains readable");
        Check.Equal(
            replacementVerifier,
            replacedCredential!.Verifier,
            "credential replacement persists exactly once");

        await store.MarkAccountOnlineAsync(created.Id);
        Check.Equal(
            (short)1,
            await ReadLoginStatusAsync(dataSource, created.Id),
            "presence compatibility writer marks the account online");
        await store.MarkAccountOfflineAsync(created.Id);
        Check.Equal(
            (short)0,
            await ReadLoginStatusAsync(dataSource, created.Id),
            "presence compatibility writer marks the account offline");

        var firstPresenceToken = Guid.NewGuid();
        var replacementPresenceToken = Guid.NewGuid();
        await store.MarkAccountPlayerOnlineAsync(
            created.Id,
            firstPresenceToken);
        await store.MarkAccountPlayerOnlineAsync(
            created.Id,
            replacementPresenceToken);
        Check.True(
            !await store.TryMarkAccountPlayerOfflineAsync(
                created.Id,
                firstPresenceToken),
            "a stale process cannot clear replacement presence");
        _ = await store.LoginOrCreateLegacyAccountAsync(
            username,
            "must-not-replace-fenced-presence");
        await store.MarkAccountOfflineAsync(created.Id);
        var replacementPresence = await ReadPresenceAsync(
            dataSource,
            created.Id);
        Check.True(
            replacementPresence.LoginStatus == 1 &&
            replacementPresence.Token == replacementPresenceToken,
            "legacy login/disconnect cannot mutate fenced replacement presence");
        Check.True(
            await store.TryMarkAccountPlayerOfflineAsync(
                created.Id,
                replacementPresenceToken),
            "the current process clears its exact presence token");
        var releasedPresence = await ReadPresenceAsync(
            dataSource,
            created.Id);
        Check.True(
            releasedPresence.LoginStatus == 0 &&
            releasedPresence.Token is null,
            "final replacement exit leaves the account offline");

        var legacyLogin =
            await store.LoginOrCreateLegacyAccountAsync(
                username,
                "must-not-replace-versioned-verifier");
        Check.Equal(
            created,
            legacyLogin,
            "legacy login resolves the existing exact-case identity");
        var preservedCredential =
            await store.FindAccountCredentialAsync(username);
        Check.True(
            preservedCredential is not null,
            "legacy login leaves the versioned credential readable");
        Check.Equal(
            replacementVerifier,
            preservedCredential!.Verifier,
            "legacy login cannot downgrade a versioned verifier");
    }

    private static async Task<AccountIdentity>
        AssertLegacyCompatibilityAsync(
        NpgsqlDataSource dataSource,
        PostgresAccountStore store,
        string username)
    {
        var account = await store.LoginOrCreateLegacyAccountAsync(
            username,
            LegacyPassword);
        var stored = await store.FindAccountCredentialAsync(username);
        Check.True(
            stored is not null,
            "legacy compatibility credential remains readable");

        Check.Equal(
            username,
            account.Username,
            "legacy compatibility creation preserves username casing");
        Check.Equal(
            LegacyPassword,
            stored!.Verifier,
            "legacy compatibility path retains its raw local credential");
        Check.Equal(
            (short)1,
            await ReadLoginStatusAsync(dataSource, account.Id),
            "legacy compatibility login maintains login_status");
        return account;
    }

    private static async Task<int> AssertSemanticGatewayRouteAsync(
        NpgsqlDataSource dataSource,
        int accountId,
        string token)
    {
        var characterName = $"B20B{token}";
        await using var insert = dataSource.CreateCommand("""
            INSERT INTO public.character_base (
                account_id,
                server_id,
                name,
                "Map"
            )
            VALUES (
                @accountId,
                1,
                @characterName,
                @mapId
            )
            RETURNING id;
            """);
        insert.Parameters.AddWithValue("accountId", accountId);
        insert.Parameters.AddWithValue("characterName", characterName);
        insert.Parameters.AddWithValue("mapId", 5);
        var characterId = Convert.ToInt32(
            await insert.ExecuteScalarAsync());

        var routes =
            new PostgresSemanticGatewayCharacterRouteReader(dataSource);
        var route = await routes.FindCharacterRouteAsync(
            accountId,
            RealmId.Tempest);
        Check.True(
            route is not null,
            "focused semantic-gateway reader resolves the active route");
        Check.Equal(
            characterId,
            route!.CharacterId,
            "semantic-gateway route preserves character identity");
        Check.Equal(
            MapId.FromLegacy(5),
            route.MapId,
            "semantic-gateway route preserves authoritative map identity");
        return characterId;
    }

    private static async Task<short> ReadLoginStatusAsync(
        NpgsqlDataSource dataSource,
        int accountId)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT login_status
            FROM public.accounts
            WHERE id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        return Convert.ToInt16(await command.ExecuteScalarAsync());
    }

    private static async Task<AccountPresenceProjection>
        ReadPresenceAsync(
            NpgsqlDataSource dataSource,
            int accountId)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT login_status, login_presence_token
            FROM public.accounts
            WHERE id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                "Account presence fixture disappeared.");
        }
        return new AccountPresenceProjection(
            reader.GetInt16(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }

    private static async Task<long> ReadAccountCountAsync(
        NpgsqlDataSource dataSource,
        string username)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT count(*)
            FROM public.accounts
            WHERE username = @username;
            """);
        command.Parameters.AddWithValue("username", username);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task DeleteFixturesAsync(
        NpgsqlDataSource dataSource,
        string[] usernames)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM public.accounts
            WHERE username = ANY(@usernames);
            """);
        command.Parameters.AddWithValue("usernames", usernames);
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateStructuralVerifier(
        int iterations,
        byte fill)
    {
        Span<byte> salt = stackalloc byte[
            AuthenticationOptions.PasswordSaltBytes];
        Span<byte> hash = stackalloc byte[
            AuthenticationOptions.PasswordHashBytes];
        salt.Fill(fill);
        hash.Fill((byte)(fill ^ 0xA5));
        try
        {
            using var record = PasswordVerifierRecord.Create(
                iterations,
                salt,
                hash);
            return record.Encode();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private readonly record struct AccountPresenceProjection(
        short LoginStatus,
        Guid? Token);
}
