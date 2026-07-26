using System.Text;

namespace Godswar.Server.Networking.Secure;

internal sealed partial class SecureNetworkOptions
{
    private const int MaximumCertificatePasswordUtf8Bytes = 4_096;
    private const int MaximumCertificatePasswordFileBytes =
        MaximumCertificatePasswordUtf8Bytes + 2;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string ResolveCertificatePassword(string fallback)
    {
        var direct = Environment.GetEnvironmentVariable(
            DefaultCertificatePasswordEnvironmentVariable);
        if (direct is not null)
        {
            return direct;
        }

        var filePath = Environment.GetEnvironmentVariable(
            DefaultCertificatePasswordFileEnvironmentVariable);
        return filePath is null
            ? fallback
            : ReadCertificatePasswordFile(filePath);
    }

    private static string ReadCertificatePasswordFile(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) ||
            !Path.IsPathFullyQualified(configuredPath))
        {
            throw new InvalidDataException(
                $"{DefaultCertificatePasswordFileEnvironmentVariable} " +
                "must name an absolute file path.");
        }

        var path = Path.GetFullPath(configuredPath);
        var bytes =
            new byte[MaximumCertificatePasswordFileBytes + 1];
        var byteCount = 0;
        try
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1,
                    FileOptions.SequentialScan);
                if (stream.Length > MaximumCertificatePasswordFileBytes)
                {
                    throw PasswordFileTooLarge();
                }

                while (byteCount < bytes.Length)
                {
                    var read = stream.Read(
                        bytes,
                        byteCount,
                        bytes.Length - byteCount);
                    if (read == 0)
                    {
                        break;
                    }

                    byteCount += read;
                }

                if (byteCount > MaximumCertificatePasswordFileBytes ||
                    stream.ReadByte() != -1)
                {
                    throw PasswordFileTooLarge();
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception error)
                when (error is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException(
                    "The configured secure-network certificate password file " +
                    "could not be read.",
                    error);
            }

            string password;
            try
            {
                password = StrictUtf8.GetString(
                    bytes,
                    0,
                    byteCount);
            }
            catch (DecoderFallbackException error)
            {
                throw new InvalidDataException(
                    "The secure-network certificate password file must " +
                    "contain valid UTF-8 text.",
                    error);
            }

            password = TrimOneTerminalNewline(password);
            if (password.Length == 0)
            {
                throw new InvalidDataException(
                    "The secure-network certificate password file is empty.");
            }
            if (password.Contains('\0', StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The secure-network certificate password file contains " +
                    "a NUL character.");
            }
            if (StrictUtf8.GetByteCount(password) >
                MaximumCertificatePasswordUtf8Bytes)
            {
                throw PasswordFileTooLarge();
            }

            return password;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                bytes);
        }
    }

    private static string TrimOneTerminalNewline(string value)
    {
        if (value.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return value[..^2];
        }
        if (value.EndsWith('\r') || value.EndsWith('\n'))
        {
            return value[..^1];
        }

        return value;
    }

    private static InvalidDataException PasswordFileTooLarge() =>
        new(
            "The secure-network certificate password file exceeds the " +
            $"{MaximumCertificatePasswordUtf8Bytes}-byte password limit.");
}
