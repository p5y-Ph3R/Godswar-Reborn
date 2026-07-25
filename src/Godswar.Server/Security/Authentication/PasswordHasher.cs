using System.Security.Cryptography;

namespace Godswar.Server.Security.Authentication;

internal enum VersionedPasswordVerificationStatus
{
    Verified,
    Rejected,
    Malformed,
    CostOutOfRange
}

internal readonly record struct VersionedPasswordVerification(
    VersionedPasswordVerificationStatus Status,
    bool NeedsUpgrade)
{
    public bool IsVerified =>
        Status == VersionedPasswordVerificationStatus.Verified;
}

internal sealed class PasswordHasher
{
    private static readonly byte[] DummySalt =
    [
        0x47, 0x57, 0x53, 0x2D, 0x41, 0x55, 0x54, 0x48,
        0x2D, 0x44, 0x55, 0x4D, 0x4D, 0x59, 0x2D, 0x31
    ];

    private readonly AuthenticationPolicy _policy;
    private readonly IPasswordKdfScheduler _scheduler;

    public PasswordHasher(
        AuthenticationPolicy policy,
        IPasswordKdfScheduler scheduler)
    {
        _policy = policy;
        _scheduler = scheduler ??
            throw new ArgumentNullException(nameof(scheduler));
    }

    public async Task<string> CreateVerifierAsync(
        ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken)
    {
        var salt = new byte[AuthenticationOptions.PasswordSaltBytes];
        RandomNumberGenerator.Fill(salt);
        byte[]? hash = null;
        try
        {
            hash = await _scheduler.DeriveAsync(
                password,
                salt,
                _policy.Iterations,
                cancellationToken);
            RequireHashLength(hash);
            using var record = PasswordVerifierRecord.Create(
                _policy.Iterations,
                salt,
                hash);
            return record.Encode();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            if (hash is not null)
            {
                CryptographicOperations.ZeroMemory(hash);
            }
        }
    }

    public async Task<VersionedPasswordVerification> VerifyAsync(
        ReadOnlyMemory<byte> password,
        string encoded,
        CancellationToken cancellationToken)
    {
        if (!PasswordVerifierRecord.TryParse(encoded, out var record))
        {
            return new VersionedPasswordVerification(
                VersionedPasswordVerificationStatus.Malformed,
                NeedsUpgrade: false);
        }

        using var parsed = record!;
        if (parsed.Iterations < _policy.MinimumStoredIterations ||
            parsed.Iterations > _policy.MaximumStoredIterations)
        {
            return new VersionedPasswordVerification(
                VersionedPasswordVerificationStatus.CostOutOfRange,
                NeedsUpgrade: false);
        }

        var salt = new byte[AuthenticationOptions.PasswordSaltBytes];
        byte[]? candidate = null;
        try
        {
            if (!parsed.TryCopySalt(salt))
            {
                throw new InvalidOperationException(
                    "Parsed verifier did not expose its fixed salt.");
            }

            candidate = await _scheduler.DeriveAsync(
                password,
                salt,
                parsed.Iterations,
                cancellationToken);
            RequireHashLength(candidate);
            var matches = parsed.FixedTimeEqualsHash(candidate);
            return new VersionedPasswordVerification(
                matches
                    ? VersionedPasswordVerificationStatus.Verified
                    : VersionedPasswordVerificationStatus.Rejected,
                NeedsUpgrade: matches &&
                    parsed.Iterations < _policy.Iterations);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            if (candidate is not null)
            {
                CryptographicOperations.ZeroMemory(candidate);
            }
        }
    }

    public async Task RunDummyAsync(
        ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken)
    {
        var result = await _scheduler.DeriveAsync(
            password,
            DummySalt,
            _policy.Iterations,
            cancellationToken);
        try
        {
            RequireHashLength(result);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(result);
        }
    }

    private static void RequireHashLength(byte[] hash)
    {
        if (hash.Length != AuthenticationOptions.PasswordHashBytes)
        {
            CryptographicOperations.ZeroMemory(hash);
            throw new CryptographicException(
                "Password KDF returned an unexpected output length.");
        }
    }
}
