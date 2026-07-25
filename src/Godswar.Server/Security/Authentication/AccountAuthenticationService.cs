using System.Diagnostics;
using System.Security.Cryptography;
using Godswar.Server.State;

namespace Godswar.Server.Security.Authentication;

internal sealed class AccountAuthenticationService : IAsyncDisposable
{
    private readonly IGameStore _store;
    private readonly AuthenticationPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly IPasswordKdfScheduler _scheduler;
    private readonly bool _ownsScheduler;
    private readonly PasswordHasher _hasher;

    public AccountAuthenticationService(
        IGameStore store,
        AuthenticationOptions options,
        TimeProvider? timeProvider = null,
        IPasswordKdfScheduler? scheduler = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _policy = (options ??
            throw new ArgumentNullException(nameof(options))).Snapshot();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _scheduler = scheduler ??
            new PasswordKdfScheduler(_policy, _timeProvider);
        _ownsScheduler = scheduler is null;
        _hasher = new PasswordHasher(_policy, _scheduler);
    }

    public async Task<AccountAuthenticationResult> AuthenticateAsync(
        string username,
        ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken = default)
    {
        var started = _timeProvider.GetTimestamp();
        if (!IsValidUsername(username) ||
            !IsValidPassword(password.Span))
        {
            return Complete(
                new AccountAuthenticationResult(
                    AccountAuthenticationStatus.InvalidInput),
                AuthenticationMetricOutcome.InvalidInput,
                started);
        }

        using var operationDeadline = new CancellationTokenSource(
            _policy.OperationTimeout,
            _timeProvider);
        using var operationLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                operationDeadline.Token);
        try
        {
            var stored = await _store.FindAccountCredentialAsync(
                username,
                operationLifetime.Token);
            var result = stored is null
                ? await AuthenticateMissingAsync(
                    username,
                    password,
                    operationLifetime.Token)
                : await AuthenticateExistingAsync(
                    stored,
                    password,
                    operationLifetime.Token);
            return Complete(
                result,
                ToMetricOutcome(result.Status),
                started);
        }
        catch (PasswordKdfAdmissionException)
        {
            return Complete(
                new AccountAuthenticationResult(
                    AccountAuthenticationStatus.Busy),
                AuthenticationMetricOutcome.Busy,
                started);
        }
        catch (OperationCanceledException)
            when (operationDeadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            return Complete(
                new AccountAuthenticationResult(
                    AccountAuthenticationStatus.TimedOut),
                AuthenticationMetricOutcome.TimedOut,
                started);
        }
        catch (OperationCanceledException)
        {
            AuthenticationMetrics.Record(
                AuthenticationMetricOutcome.Cancelled,
                _timeProvider.GetElapsedTime(started));
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsScheduler)
        {
            await _scheduler.DisposeAsync();
        }
    }

    private async Task<AccountAuthenticationResult>
        AuthenticateMissingAsync(
            string username,
            ReadOnlyMemory<byte> password,
            CancellationToken cancellationToken)
    {
        await _hasher.RunDummyAsync(password, cancellationToken);
        if (!_policy.AllowRegistration)
        {
            return new AccountAuthenticationResult(
                AccountAuthenticationStatus.Rejected);
        }

        var verifier = await _hasher.CreateVerifierAsync(
            password,
            cancellationToken);
        var account = await _store.TryCreateAccountWithCredentialAsync(
            username,
            verifier,
            cancellationToken);
        if (account is null)
        {
            var concurrent = await _store.FindAccountCredentialAsync(
                username,
                cancellationToken);
            return concurrent is null
                ? new AccountAuthenticationResult(
                    AccountAuthenticationStatus.Rejected)
                : await AuthenticateExistingAsync(
                    concurrent,
                    password,
                    cancellationToken);
        }

        await _store.MarkAccountOnlineAsync(
            account.Id,
            cancellationToken);
        return new AccountAuthenticationResult(
            AccountAuthenticationStatus.Accepted,
            account,
            AccountCreated: true);
    }

    private async Task<AccountAuthenticationResult>
        AuthenticateExistingAsync(
            StoredAccountCredential stored,
            ReadOnlyMemory<byte> password,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(stored.Verifier))
        {
            await _hasher.RunDummyAsync(password, cancellationToken);
            return new AccountAuthenticationResult(
                AccountAuthenticationStatus.PasswordResetRequired);
        }

        if (PasswordVerifierRecord.IsVersionedCandidate(
                stored.Verifier))
        {
            return await AuthenticateCurrentVersionAsync(
                stored,
                password,
                cancellationToken);
        }

        await _hasher.RunDummyAsync(password, cancellationToken);
        if (!_policy.AllowPlaintextMigration ||
            !FixedTimeEqualsLegacyPlaintext(
                stored.Verifier,
                password.Span))
        {
            return new AccountAuthenticationResult(
                _policy.AllowPlaintextMigration
                    ? AccountAuthenticationStatus.Rejected
                    : AccountAuthenticationStatus.PasswordResetRequired);
        }

        var replacement = await _hasher.CreateVerifierAsync(
            password,
            cancellationToken);
        var migrated =
            await _store.TryReplaceAccountCredentialAsync(
                stored.Account.Id,
                stored.Verifier,
                replacement,
                cancellationToken);
        if (!migrated)
        {
            var concurrent = await _store.FindAccountCredentialAsync(
                stored.Account.Username,
                cancellationToken);
            return concurrent is null
                ? new AccountAuthenticationResult(
                    AccountAuthenticationStatus.Rejected)
                : await AuthenticateCurrentVersionAsync(
                    concurrent,
                    password,
                    cancellationToken);
        }

        await _store.MarkAccountOnlineAsync(
            stored.Account.Id,
            cancellationToken);
        return new AccountAuthenticationResult(
            AccountAuthenticationStatus.Accepted,
            stored.Account,
            CredentialMigrated: true);
    }

    private async Task<AccountAuthenticationResult>
        AuthenticateCurrentVersionAsync(
            StoredAccountCredential stored,
            ReadOnlyMemory<byte> password,
            CancellationToken cancellationToken,
            bool allowUpgrade = true)
    {
        var verification = await _hasher.VerifyAsync(
            password,
            stored.Verifier,
            cancellationToken);
        if (verification.Status is
            VersionedPasswordVerificationStatus.Malformed or
            VersionedPasswordVerificationStatus.CostOutOfRange)
        {
            await _hasher.RunDummyAsync(password, cancellationToken);
            return new AccountAuthenticationResult(
                AccountAuthenticationStatus.PasswordResetRequired);
        }
        if (!verification.IsVerified)
        {
            return new AccountAuthenticationResult(
                AccountAuthenticationStatus.Rejected);
        }

        var migrated = false;
        if (allowUpgrade && verification.NeedsUpgrade)
        {
            var replacement = await _hasher.CreateVerifierAsync(
                password,
                cancellationToken);
            migrated = await _store.TryReplaceAccountCredentialAsync(
                stored.Account.Id,
                stored.Verifier,
                replacement,
                cancellationToken);
            if (!migrated)
            {
                var concurrent =
                    await _store.FindAccountCredentialAsync(
                        stored.Account.Username,
                        cancellationToken);
                return concurrent is null
                    ? new AccountAuthenticationResult(
                        AccountAuthenticationStatus.Rejected)
                    : await AuthenticateCurrentVersionAsync(
                        concurrent,
                        password,
                        cancellationToken,
                        allowUpgrade: false);
            }
        }

        await _store.MarkAccountOnlineAsync(
            stored.Account.Id,
            cancellationToken);
        return new AccountAuthenticationResult(
            AccountAuthenticationStatus.Accepted,
            stored.Account,
            CredentialMigrated: migrated);
    }

    private AccountAuthenticationResult Complete(
        AccountAuthenticationResult result,
        AuthenticationMetricOutcome outcome,
        long started)
    {
        AuthenticationMetrics.Record(
            outcome,
            _timeProvider.GetElapsedTime(started));
        return result;
    }

    private static AuthenticationMetricOutcome ToMetricOutcome(
        AccountAuthenticationStatus status) =>
        status switch
        {
            AccountAuthenticationStatus.Accepted =>
                AuthenticationMetricOutcome.Accepted,
            AccountAuthenticationStatus.Rejected =>
                AuthenticationMetricOutcome.Rejected,
            AccountAuthenticationStatus.PasswordResetRequired =>
                AuthenticationMetricOutcome.ResetRequired,
            AccountAuthenticationStatus.InvalidInput =>
                AuthenticationMetricOutcome.InvalidInput,
            AccountAuthenticationStatus.Busy =>
                AuthenticationMetricOutcome.Busy,
            AccountAuthenticationStatus.TimedOut =>
                AuthenticationMetricOutcome.TimedOut,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private static bool IsValidUsername(string? username)
    {
        return username is
        {
            Length: >= 1 and <=
            AuthenticationOptions.MaximumUsernameBytes
        } &&
            username.All(static character =>
                character is >= '!' and <= '~');
    }

    private static bool IsValidPassword(ReadOnlySpan<byte> password)
    {
        if (password.Length is < 1 or >
                AuthenticationOptions.MaximumPasswordBytes)
        {
            return false;
        }

        foreach (var value in password)
        {
            if (value is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static bool FixedTimeEqualsLegacyPlaintext(
        string stored,
        ReadOnlySpan<byte> supplied)
    {
        Span<byte> storedBytes = stackalloc byte[
            AuthenticationOptions.MaximumPasswordBytes + 1];
        Span<byte> suppliedBytes = stackalloc byte[
            AuthenticationOptions.MaximumPasswordBytes + 1];
        try
        {
            var valid = stored.Length is >= 1 and <=
                AuthenticationOptions.MaximumPasswordBytes;
            if (valid)
            {
                storedBytes[0] = (byte)stored.Length;
                for (var index = 0; index < stored.Length; index++)
                {
                    var character = stored[index];
                    if (character is < ' ' or > '~')
                    {
                        valid = false;
                        break;
                    }

                    storedBytes[index + 1] = (byte)character;
                }
            }

            suppliedBytes[0] = (byte)supplied.Length;
            supplied.CopyTo(suppliedBytes[1..]);
            var equal = CryptographicOperations.FixedTimeEquals(
                storedBytes,
                suppliedBytes);
            return valid && equal;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(storedBytes);
            CryptographicOperations.ZeroMemory(suppliedBytes);
        }
    }
}
