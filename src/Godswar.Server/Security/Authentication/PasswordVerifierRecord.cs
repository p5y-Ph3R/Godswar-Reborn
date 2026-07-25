using System.Globalization;
using System.Security.Cryptography;

namespace Godswar.Server.Security.Authentication;

internal sealed class PasswordVerifierRecord : IDisposable
{
    internal const string VersionedPrefix = "gws$";
    private const string Algorithm = "pbkdf2-sha256";
    private const string Version = "v1";
    private readonly byte[] _salt;
    private readonly byte[] _hash;
    private bool _disposed;

    private PasswordVerifierRecord(
        int iterations,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> hash)
    {
        Iterations = iterations;
        _salt = salt.ToArray();
        _hash = hash.ToArray();
    }

    public int Iterations { get; }

    public bool IsDisposed => _disposed;

    public static bool IsVersionedCandidate(string? encoded) =>
        encoded?.StartsWith(VersionedPrefix, StringComparison.Ordinal) == true;

    public static PasswordVerifierRecord Create(
        int iterations,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> hash)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
        if (salt.Length != AuthenticationOptions.PasswordSaltBytes)
        {
            throw new ArgumentException(
                $"Password salt must be exactly {AuthenticationOptions.PasswordSaltBytes} bytes.",
                nameof(salt));
        }
        if (hash.Length != AuthenticationOptions.PasswordHashBytes)
        {
            throw new ArgumentException(
                $"Password hash must be exactly {AuthenticationOptions.PasswordHashBytes} bytes.",
                nameof(hash));
        }

        return new PasswordVerifierRecord(iterations, salt, hash);
    }

    public static bool TryParse(
        string? encoded,
        out PasswordVerifierRecord? record)
    {
        record = null;
        if (string.IsNullOrEmpty(encoded) ||
            encoded.Length > 255 ||
            !IsVersionedCandidate(encoded))
        {
            return false;
        }

        var segments = encoded.Split('$', StringSplitOptions.None);
        if (segments.Length != 6 ||
            !segments[0].Equals("gws", StringComparison.Ordinal) ||
            !segments[1].Equals(Algorithm, StringComparison.Ordinal) ||
            !segments[2].Equals(Version, StringComparison.Ordinal) ||
            !TryParseIterations(segments[3], out var iterations))
        {
            return false;
        }

        Span<byte> salt = stackalloc byte[
            AuthenticationOptions.PasswordSaltBytes];
        Span<byte> hash = stackalloc byte[
            AuthenticationOptions.PasswordHashBytes];
        try
        {
            if (!TryDecodeCanonicalBase64(segments[4], salt) ||
                !TryDecodeCanonicalBase64(segments[5], hash))
            {
                return false;
            }

            record = new PasswordVerifierRecord(iterations, salt, hash);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public string Encode()
    {
        ThrowIfDisposed();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"gws${Algorithm}${Version}${Iterations}$" +
            $"{Convert.ToBase64String(_salt)}${Convert.ToBase64String(_hash)}");
    }

    public bool TryCopySalt(Span<byte> destination)
    {
        if (_disposed ||
            destination.Length < AuthenticationOptions.PasswordSaltBytes)
        {
            return false;
        }

        _salt.CopyTo(
            destination[..AuthenticationOptions.PasswordSaltBytes]);
        return true;
    }

    public bool FixedTimeEqualsHash(ReadOnlySpan<byte> candidate)
    {
        ThrowIfDisposed();
        return candidate.Length == _hash.Length &&
            CryptographicOperations.FixedTimeEquals(candidate, _hash);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_salt);
        CryptographicOperations.ZeroMemory(_hash);
        _disposed = true;
    }

    private static bool TryParseIterations(
        string encoded,
        out int iterations)
    {
        iterations = 0;
        return encoded.Length is >= 1 and <= 8 &&
            encoded[0] is >= '1' and <= '9' &&
            encoded.All(static character => character is >= '0' and <= '9') &&
            int.TryParse(
                encoded,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out iterations) &&
            iterations > 0;
    }

    private static bool TryDecodeCanonicalBase64(
        string encoded,
        Span<byte> destination)
    {
        var expectedLength = ((destination.Length + 2) / 3) * 4;
        if (encoded.Length != expectedLength ||
            !Convert.TryFromBase64String(
                encoded,
                destination,
                out var written) ||
            written != destination.Length)
        {
            return false;
        }

        return Convert.ToBase64String(destination)
            .Equals(encoded, StringComparison.Ordinal);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
