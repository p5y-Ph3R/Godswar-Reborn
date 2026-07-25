using System.Security.Cryptography;
using Godswar.Server.Security.Authentication;

namespace Godswar.Server.ProtocolChecks;

internal static class PasswordAuthenticationPrimitiveChecks
{
    public static Task RunAsync()
    {
        CheckOptions();
        CheckVerifierRecord();
        CheckStrictVerifierRejection();
        CheckPbkdf2GoldenVector();
        CheckMetricTags();
        return Task.CompletedTask;
    }

    private static void CheckOptions()
    {
        var options = new AuthenticationOptions();
        var policy = options.Snapshot();
        Check.Equal(
            600_000,
            policy.Iterations,
            "default password work factor");
        Check.Equal(
            16,
            AuthenticationOptions.PasswordSaltBytes,
            "password salt size");
        Check.Equal(
            32,
            AuthenticationOptions.PasswordHashBytes,
            "password result size");
        Check.Equal(64, policy.QueueCapacity, "KDF queue item bound");
        Check.Equal(
            8 * 1024,
            policy.QueueCredentialBytes,
            "KDF queue credential-byte bound");
        Check.Equal(
            TimeSpan.FromMilliseconds(250),
            policy.QueueAdmissionTimeout,
            "KDF finite admission timeout");
        Check.Equal(
            TimeSpan.FromSeconds(5),
            policy.OperationTimeout,
            "authentication absolute deadline");
        Check.True(
            policy.MaximumConcurrentKdfs is >= 1 and <= 16,
            "automatic KDF concurrency is bounded");
        Check.True(
            !policy.AllowRegistration,
            "self-registration defaults off");

        Check.Throws<InvalidDataException>(
            () => new AuthenticationOptions
            {
                Iterations = 99_999
            }.Validate(),
            "weak configured work factor is rejected");
        Check.Throws<InvalidDataException>(
            () => new AuthenticationOptions
            {
                MaximumStoredIterations = 500_000
            }.Validate(),
            "desired work cannot exceed stored-cost ceiling");
        Check.Throws<InvalidDataException>(
            () => new AuthenticationOptions
            {
                MaximumConcurrentKdfs = 17
            }.Validate(),
            "KDF concurrency cannot exceed hard bound");
        Check.Throws<InvalidDataException>(
            () => new AuthenticationOptions
            {
                QueueCapacity = 0
            }.Validate(),
            "KDF queue requires finite positive capacity");
        Check.Throws<InvalidDataException>(
            () => new AuthenticationOptions
            {
                QueueCredentialBytes = 31
            }.Validate(),
            "credential byte budget accepts one maximum password");
        Check.Throws<InvalidDataException>(
            () => new AuthenticationOptions
            {
                QueueAdmissionTimeoutMilliseconds = 1_000,
                OperationTimeoutMilliseconds = 1_000
            }.Validate(),
            "operation deadline must exceed queue admission");
    }

    private static void CheckVerifierRecord()
    {
        var salt = Enumerable.Range(0, 16)
            .Select(static value => (byte)value)
            .ToArray();
        var hash = Enumerable.Range(0, 32)
            .Select(static value => (byte)(0xA0 + value))
            .ToArray();
        try
        {
            using var original = PasswordVerifierRecord.Create(
                600_000,
                salt,
                hash);
            var encoded = original.Encode();
            Check.Equal(
                "gws$pbkdf2-sha256$v1$600000$" +
                "AAECAwQFBgcICQoLDA0ODw==$" +
                "oKGio6SlpqeoqaqrrK2ur7C" +
                "xsrO0tba3uLm6u7y9vr8=",
                encoded,
                "versioned password verifier canonical encoding");
            Check.True(
                PasswordVerifierRecord.TryParse(
                    encoded,
                    out var parsed),
                "canonical verifier parses");
            using var decoded = parsed!;
            var decodedSalt = new byte[16];
            try
            {
                Check.True(
                    decoded.TryCopySalt(decodedSalt),
                    "parsed verifier exposes fixed salt");
                Check.True(
                    decodedSalt.SequenceEqual(salt),
                    "parsed verifier salt round trip");
                Check.True(
                    decoded.FixedTimeEqualsHash(hash),
                    "parsed verifier hash round trip");
                var wrong = hash.ToArray();
                wrong[^1] ^= 0x80;
                try
                {
                    Check.True(
                        !decoded.FixedTimeEqualsHash(wrong),
                        "hash mismatch is rejected");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(wrong);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decodedSalt);
            }

            original.Dispose();
            Check.True(
                original.IsDisposed,
                "verifier disposal is observable");
            Check.Throws<ObjectDisposedException>(
                () => original.Encode(),
                "disposed verifier cannot be reused");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static void CheckStrictVerifierRejection()
    {
        string[] malformed =
        [
            "",
            "plaintext",
            "gws$",
            "gws$pbkdf2-sha256$v2$600000$" +
                "AAECAwQFBgcICQoLDA0ODw==$" +
                new string('A', 44),
            "gws$pbkdf2-sha1$v1$600000$" +
                "AAECAwQFBgcICQoLDA0ODw==$" +
                new string('A', 44),
            "gws$pbkdf2-sha256$v1$0600000$" +
                "AAECAwQFBgcICQoLDA0ODw==$" +
                new string('A', 44),
            "gws$pbkdf2-sha256$v1$600000$short$" +
                new string('A', 44),
            "gws$pbkdf2-sha256$v1$600000$" +
                "AAECAwQFBgcICQoLDA0ODw== $" +
                new string('A', 44),
            "gws$pbkdf2-sha256$v1$600000$" +
                "AAECAwQFBgcICQoLDA0ODw==$" +
                new string('A', 43),
            "gws$pbkdf2-sha256$v1$600000$" +
                "AAECAwQFBgcICQoLDA0ODw==$" +
                new string('A', 44) + "$trailing"
        ];

        foreach (var value in malformed)
        {
            Check.True(
                !PasswordVerifierRecord.TryParse(value, out _),
                $"malformed verifier is rejected: length={value.Length}");
        }
        Check.True(
            PasswordVerifierRecord.IsVersionedCandidate("gws$bad"),
            "corrupt versioned prefix cannot fall back to plaintext");
        Check.True(
            !PasswordVerifierRecord.IsVersionedCandidate("plaintext"),
            "legacy plaintext remains distinguishable");
    }

    private static void CheckPbkdf2GoldenVector()
    {
        var password = "password"u8.ToArray();
        var salt = "salt"u8.ToArray();
        byte[]? result = null;
        try
        {
            result = new Pbkdf2Sha256KeyDeriver().Derive(
                password,
                salt,
                iterations: 1);
            Check.Equal(
                "120FB6CFFCF8B32C43E7225256C4F837" +
                "A86548C92CCC35480805987CB70BE17B",
                Convert.ToHexString(result),
                "PBKDF2-HMAC-SHA256 golden vector");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(salt);
            if (result is not null)
            {
                CryptographicOperations.ZeroMemory(result);
            }
        }
    }

    private static void CheckMetricTags()
    {
        foreach (var outcome in Enum.GetValues<
                     AuthenticationMetricOutcome>())
        {
            var tag = AuthenticationMetrics.ToMetricTag(outcome);
            Check.True(
                tag.Length is >= 4 and <= 32 &&
                tag.All(static character =>
                    character is >= 'a' and <= 'z' or '_'),
                "authentication metric tags remain finite");
        }
    }

}
