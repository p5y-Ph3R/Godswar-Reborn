using System.Security.Cryptography;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class RegistrationCollisionAuthenticationChecks
{
    public static async Task RunAsync()
    {
        const string username = "registration-collision";
        const string legacyPassword = "collision-password";
        var store = new LegacyCollisionStore(
            username,
            legacyPassword);
        var options = new AuthenticationOptions
        {
            Iterations = 100_000,
            MinimumStoredIterations = 100_000,
            MaximumStoredIterations = 200_000,
            MaximumConcurrentKdfs = 1,
            QueueCapacity = 4,
            QueueCredentialBytes = 128,
            QueueAdmissionTimeoutMilliseconds = 250,
            OperationTimeoutMilliseconds = 5_000,
            AllowRegistration = true
        };
        await using var service =
            new AccountAuthenticationService(store, options);
        var password = "collision-password"u8.ToArray();
        try
        {
            var result = await service.AuthenticateAsync(
                username,
                password);

            Check.True(
                result.IsAccepted,
                "registration collision with matching plaintext accepts");
            Check.True(
                !result.AccountCreated,
                "collision winner is not reported as locally created");
            Check.True(
                result.CredentialMigrated,
                "colliding plaintext credential is migrated");
            Check.Equal(
                1,
                store.CreateAttempts,
                "registration attempts one conflict-safe create");
            Check.Equal(
                1,
                store.CredentialReplacementCount,
                "plaintext collision is replaced through credential CAS");
            Check.Equal(
                1,
                store.MarkOnlineCount,
                "accepted collision marks the account online once");
            Check.True(
                PasswordVerifierRecord.TryParse(
                    store.StoredVerifier,
                    out var parsed),
                "collision migration leaves a versioned verifier");
            parsed!.Dispose();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    private sealed class LegacyCollisionStore : GameStoreTestStub
    {
        private readonly GameAccount _account;
        private readonly string _legacyPassword;
        private string? _storedVerifier;
        private bool _initialLookupCompleted;

        public LegacyCollisionStore(
            string username,
            string legacyPassword)
        {
            _legacyPassword = legacyPassword;
            _account = new GameAccount
            {
                Id = 7401,
                Username = username,
                CreatedUtc = DateTime.UnixEpoch
            };
        }

        public int CreateAttempts { get; private set; }

        public int CredentialReplacementCount { get; private set; }

        public int MarkOnlineCount { get; private set; }

        public string StoredVerifier =>
            _storedVerifier ?? string.Empty;

        public override Task<StoredAccountCredential?>
            FindAccountCredentialAsync(
                string username,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check.Equal(
                _account.Username,
                username,
                "collision lookup keeps the requested username");
            if (!_initialLookupCompleted)
            {
                _initialLookupCompleted = true;
                return Task.FromResult<StoredAccountCredential?>(null);
            }

            var credential = _storedVerifier is null
                ? null
                : new StoredAccountCredential(
                    CopyAccount(),
                    _storedVerifier);
            return Task.FromResult(credential);
        }

        public override Task<GameAccount?>
            TryCreateAccountWithCredentialAsync(
                string username,
                string versionedVerifier,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateAttempts++;
            Check.Equal(
                _account.Username,
                username,
                "collision create keeps the requested username");
            Check.True(
                PasswordVerifierRecord.TryParse(
                    versionedVerifier,
                    out var parsed),
                "collision create receives a versioned verifier");
            parsed!.Dispose();

            _storedVerifier = _legacyPassword;
            return Task.FromResult<GameAccount?>(null);
        }

        public override Task<bool>
            TryReplaceAccountCredentialAsync(
                int accountId,
                string expectedVerifier,
                string versionedVerifier,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (accountId != _account.Id ||
                !string.Equals(
                    _storedVerifier,
                    expectedVerifier,
                    StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            _storedVerifier = versionedVerifier;
            CredentialReplacementCount++;
            return Task.FromResult(true);
        }

        public override Task MarkAccountOnlineAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check.Equal(
                _account.Id,
                accountId,
                "collision accepts the concurrent account identity");
            MarkOnlineCount++;
            return Task.CompletedTask;
        }

        private GameAccount CopyAccount() =>
            new()
            {
                Id = _account.Id,
                Username = _account.Username,
                CreatedUtc = _account.CreatedUtc
            };
    }
}
