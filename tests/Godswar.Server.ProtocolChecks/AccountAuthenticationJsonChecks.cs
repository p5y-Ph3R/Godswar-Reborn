using System.Security.Cryptography;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class AccountAuthenticationJsonChecks
{
    public static async Task RunAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-auth-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);
        try
        {
            await CheckLegacyUsernameBoundaryAsync(dataPath);
            await CheckAdditivePersistenceAsync(dataPath);
            await CheckAuthenticationFlowsAsync(dataPath);
            await CheckConcurrentMigrationAsync(dataPath);
            await CheckUpgradeOnlyAsync(dataPath);
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static async Task CheckLegacyUsernameBoundaryAsync(
        string dataPath)
    {
        await using var store = new JsonGameStore(dataPath);
        await store.EnsureSeedDataAsync();
        var legacy = (ILegacyAccountLoginStore)store;
        var statePath = Path.Combine(dataPath, "state.json");
        var baseline = await File.ReadAllBytesAsync(statePath);
        var invalidUsernames = new[]
        {
            $" \t{new string('X', AccountIdentity.MaximumUsernameLength + 1)}\r\n",
            " invalid\u007Fname "
        };

        foreach (var invalidUsername in invalidUsernames)
        {
            var rejected = false;
            try
            {
                _ = await legacy.LoginOrCreateLegacyAccountAsync(
                    invalidUsername,
                    "must-not-persist");
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            Check.True(
                rejected,
                "invalid post-trim legacy username is rejected");
            var after = await File.ReadAllBytesAsync(statePath);
            Check.True(
                baseline.AsSpan().SequenceEqual(after),
                "invalid legacy username does not mutate JSON account state");
            Check.True(
                !File.Exists(statePath + ".tmp"),
                "invalid legacy username creates no JSON temporary write");
        }

        var fallback = await legacy.LoginOrCreateLegacyAccountAsync(
            "\0 \t\r\n",
            "legacy-fallback");
        Check.Equal(
            "player",
            fallback.Username,
            "blank legacy username retains the player fallback");
    }

    private static async Task CheckAdditivePersistenceAsync(
        string dataPath)
    {
        await using var store = new JsonGameStore(dataPath);
        await store.EnsureSeedDataAsync();
        var legacy = await store.LoginOrCreateAccountAsync(
            "auth-store-legacy",
            "legacy-password");
        Check.True(
            string.IsNullOrEmpty(legacy.Password),
            "legacy store result does not expose credential");
        var stored = await store.FindAccountCredentialAsync(
            legacy.Username);
        Check.Equal(
            "legacy-password",
            stored!.Verifier,
            "credential lookup is explicit");
        Check.True(
            string.IsNullOrEmpty(stored.Account.Password),
            "credential record account projection is scrubbed");
        Check.Equal(
            legacy.Id,
            (await store.FindAccountByIdAsync(legacy.Id))!.Id,
            "account lookup by ID");
        Check.Equal(
            legacy.Id,
            (await store.FindAccountByUsernameAsync(legacy.Username))!.Id,
            "account lookup by username");

        var verifier = CreateStructuralVerifier(100_000, 0x31);
        Check.True(
            await store.TryReplaceAccountCredentialAsync(
                legacy.Id,
                "legacy-password",
                verifier),
            "credential CAS migrates expected plaintext");
        Check.True(
            !await store.TryReplaceAccountCredentialAsync(
                legacy.Id,
                "legacy-password",
                verifier),
            "credential CAS rejects stale expected value");

        _ = await store.LoginOrCreateAccountAsync(
            legacy.Username,
            string.Empty);
        Check.Equal(
            verifier,
            (await store.FindAccountCredentialAsync(legacy.Username))!
                .Verifier,
            "legacy empty-password game login cannot erase a verifier");

        var created = await store.TryCreateAccountWithCredentialAsync(
            "auth-store-created",
            CreateStructuralVerifier(100_000, 0x52));
        Check.True(created is not null, "hashed account create succeeds");
        Check.True(
            string.IsNullOrEmpty(created!.Password),
            "hashed account create returns no verifier");
        Check.True(
            await store.TryCreateAccountWithCredentialAsync(
                "auth-store-created",
                CreateStructuralVerifier(100_000, 0x53)) is null,
            "hashed account create is conflict-safe");

        var plaintextRejected = false;
        try
        {
            _ = await store.TryCreateAccountWithCredentialAsync(
                "auth-store-plaintext",
                "plaintext");
        }
        catch (ArgumentException)
        {
            plaintextRejected = true;
        }

        Check.True(
            plaintextRejected,
            "secure create accepts only versioned verifiers");
    }

    private static async Task CheckAuthenticationFlowsAsync(
        string dataPath)
    {
        await using var store = new JsonGameStore(dataPath);
        var options = TestOptions();
        await using var service =
            new AccountAuthenticationService(store, options);
        var password = "correct horse"u8.ToArray();
        var wrongPassword = "wrong horse"u8.ToArray();
        try
        {
            var legacy = await store.LoginOrCreateAccountAsync(
                "auth-flow-legacy",
                "correct horse");
            var accepted = await service.AuthenticateAsync(
                legacy.Username,
                password);
            Check.True(accepted.IsAccepted, "legacy password authenticates");
            Check.True(
                accepted.CredentialMigrated,
                "accepted plaintext is migrated exactly once");
            Check.Equal(
                legacy.Id,
                accepted.Account!.Id,
                "successful auth result exposes only the account identity");
            var migrated = (await store.FindAccountCredentialAsync(
                legacy.Username))!.Verifier;
            Check.True(
                PasswordVerifierRecord.IsVersionedCandidate(migrated),
                "legacy plaintext is replaced by versioned verifier");

            var rejected = await service.AuthenticateAsync(
                legacy.Username,
                wrongPassword);
            Check.Equal(
                (int)AccountAuthenticationStatus.Rejected,
                (int)rejected.Status,
                "wrong versioned password is rejected");
            Check.Equal(
                migrated,
                (await store.FindAccountCredentialAsync(
                    legacy.Username))!.Verifier,
                "failed authentication never mutates credential");

            _ = await store.LoginOrCreateAccountAsync(
                "auth-flow-empty",
                string.Empty);
            var reset = await service.AuthenticateAsync(
                "auth-flow-empty",
                password);
            Check.Equal(
                (int)AccountAuthenticationStatus.PasswordResetRequired,
                (int)reset.Status,
                "empty legacy credential requires explicit reset");

            var missing = await service.AuthenticateAsync(
                "auth-flow-missing",
                password);
            Check.Equal(
                (int)AccountAuthenticationStatus.Rejected,
                (int)missing.Status,
                "missing account is generically rejected");
            Check.True(
                await store.FindAccountByUsernameAsync(
                    "auth-flow-missing") is null,
                "registration remains off by default");

            _ = await store.LoginOrCreateAccountAsync(
                "auth-flow-corrupt",
                "gws$broken");
            var corrupt = await service.AuthenticateAsync(
                "auth-flow-corrupt",
                password);
            Check.Equal(
                (int)AccountAuthenticationStatus.PasswordResetRequired,
                (int)corrupt.Status,
                "malformed versioned prefix cannot become plaintext");

            var weakVerifier = CreateStructuralVerifier(99_999, 0x61);
            _ = await store.TryCreateAccountWithCredentialAsync(
                "auth-flow-weak",
                weakVerifier);
            var weak = await service.AuthenticateAsync(
                "auth-flow-weak",
                password);
            Check.Equal(
                (int)AccountAuthenticationStatus.PasswordResetRequired,
                (int)weak.Status,
                "stored KDF cost below safe bound is never executed");

            var invalid = await service.AuthenticateAsync(
                string.Empty,
                password);
            Check.Equal(
                (int)AccountAuthenticationStatus.InvalidInput,
                (int)invalid.Status,
                "invalid username is rejected before store authority");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(wrongPassword);
        }

        var registrationOptions = TestOptions();
        registrationOptions.AllowRegistration = true;
        await using var registration =
            new AccountAuthenticationService(
                store,
                registrationOptions);
        var registrationPassword = "new-account"u8.ToArray();
        try
        {
            var created = await registration.AuthenticateAsync(
                "auth-flow-created",
                registrationPassword);
            Check.True(created.IsAccepted, "enabled registration accepts");
            Check.True(created.AccountCreated, "registration is explicit");
            var verifier = (await store.FindAccountCredentialAsync(
                "auth-flow-created"))!.Verifier;
            Check.True(
                PasswordVerifierRecord.TryParse(verifier, out var parsed),
                "registration persists only a versioned verifier");
            parsed!.Dispose();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(registrationPassword);
        }
    }

    private static async Task CheckConcurrentMigrationAsync(
        string dataPath)
    {
        await using var store = new JsonGameStore(dataPath);
        _ = await store.LoginOrCreateAccountAsync(
            "auth-concurrent",
            "race-password");
        await using var service = new AccountAuthenticationService(
            store,
            TestOptions());
        var password = "race-password"u8.ToArray();
        try
        {
            var results = await Task.WhenAll(
                service.AuthenticateAsync("auth-concurrent", password),
                service.AuthenticateAsync("auth-concurrent", password));
            Check.True(
                results.All(static result => result.IsAccepted),
                "concurrent plaintext migration accepts both valid callers");
            Check.Equal(
                1,
                results.Count(static result =>
                    result.CredentialMigrated),
                "credential CAS has one migration winner");
            var stored = (await store.FindAccountCredentialAsync(
                "auth-concurrent"))!.Verifier;
            Check.True(
                PasswordVerifierRecord.TryParse(stored, out var parsed),
                "concurrent migration leaves one valid verifier");
            parsed!.Dispose();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    private static async Task CheckUpgradeOnlyAsync(string dataPath)
    {
        await using var store = new JsonGameStore(dataPath);
        var initialOptions = TestOptions();
        initialOptions.AllowRegistration = true;
        await using (var initial = new AccountAuthenticationService(
            store,
            initialOptions))
        {
            var password = "upgrade-password"u8.ToArray();
            try
            {
                Check.True(
                    (await initial.AuthenticateAsync(
                        "auth-upgrade",
                        password)).IsAccepted,
                    "initial lower-cost verifier created");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(password);
            }
        }

        var upgradeOptions = TestOptions();
        upgradeOptions.Iterations = 200_000;
        await using (var upgrade = new AccountAuthenticationService(
            store,
            upgradeOptions))
        {
            var password = "upgrade-password"u8.ToArray();
            try
            {
                var result = await upgrade.AuthenticateAsync(
                    "auth-upgrade",
                    password);
                Check.True(
                    result.IsAccepted && result.CredentialMigrated,
                    "lower accepted work factor upgrades after verification");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(password);
            }
        }

        var upgradedVerifier =
            (await store.FindAccountCredentialAsync("auth-upgrade"))!
            .Verifier;
        Check.True(
            PasswordVerifierRecord.TryParse(
                upgradedVerifier,
                out var upgraded),
            "upgraded verifier parses");
        Check.Equal(
            200_000,
            upgraded!.Iterations,
            "upgraded verifier records desired work");
        upgraded.Dispose();

        var lowerOptions = TestOptions();
        await using var lower = new AccountAuthenticationService(
            store,
            lowerOptions);
        var lowerPassword = "upgrade-password"u8.ToArray();
        try
        {
            var accepted = await lower.AuthenticateAsync(
                "auth-upgrade",
                lowerPassword);
            Check.True(accepted.IsAccepted, "higher stored work is accepted");
            Check.True(
                !accepted.CredentialMigrated,
                "higher stored work is never downgraded");
            Check.Equal(
                upgradedVerifier,
                (await store.FindAccountCredentialAsync(
                    "auth-upgrade"))!.Verifier,
                "upgrade-only policy preserves higher verifier");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(lowerPassword);
        }
    }

    private static AuthenticationOptions TestOptions()
    {
        return new AuthenticationOptions
        {
            Iterations = 100_000,
            MinimumStoredIterations = 100_000,
            MaximumStoredIterations = 200_000,
            MaximumConcurrentKdfs = 2,
            QueueCapacity = 8,
            QueueCredentialBytes = 256,
            QueueAdmissionTimeoutMilliseconds = 250,
            OperationTimeoutMilliseconds = 5_000
        };
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
}
