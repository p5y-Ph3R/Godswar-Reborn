using System.Text;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureTlsTransportChecks
{
    private static void CheckCertificatePasswordSources()
    {
        var directName =
            SecureNetworkOptions
                .DefaultCertificatePasswordEnvironmentVariable;
        var fileName =
            SecureNetworkOptions
                .DefaultCertificatePasswordFileEnvironmentVariable;
        var previousDirect =
            Environment.GetEnvironmentVariable(directName);
        var previousFile =
            Environment.GetEnvironmentVariable(fileName);
        var root = Path.Combine(
            Path.GetTempPath(),
            $"reborn-certificate-secret-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var validPath = Path.Combine(root, "certificate-password");
            File.WriteAllText(
                validPath,
                "file-secret\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Environment.SetEnvironmentVariable(directName, null);
            Environment.SetEnvironmentVariable(fileName, validPath);
            var fromFile = new SecureNetworkOptions();
            fromFile.ApplyEnvironment();
            Check.Equal(
                "file-secret",
                fromFile.CertificatePassword,
                "certificate password file trims one terminal CRLF");

            Environment.SetEnvironmentVariable(
                directName,
                "direct-secret");
            Environment.SetEnvironmentVariable(
                fileName,
                Path.Combine(root, "missing"));
            var directWins = new SecureNetworkOptions();
            directWins.ApplyEnvironment();
            Check.Equal(
                "direct-secret",
                directWins.CertificatePassword,
                "direct certificate password environment value has precedence");

            Environment.SetEnvironmentVariable(directName, string.Empty);
            var emptyDirectWins = new SecureNetworkOptions();
            emptyDirectWins.ApplyEnvironment();
            Check.Equal(
                string.Empty,
                emptyDirectWins.CertificatePassword,
                "an explicitly empty direct password does not fall back to a file");

            Environment.SetEnvironmentVariable(directName, null);
            Environment.SetEnvironmentVariable(
                fileName,
                "relative-certificate-password");
            Check.Throws<InvalidDataException>(
                () => new SecureNetworkOptions().ApplyEnvironment(),
                "certificate password file path must be absolute");

            Environment.SetEnvironmentVariable(
                fileName,
                Path.Combine(root, "missing"));
            Check.Throws<InvalidDataException>(
                () => new SecureNetworkOptions().ApplyEnvironment(),
                "certificate password file must exist and be readable");

            var emptyPath = Path.Combine(root, "empty-password");
            File.WriteAllText(emptyPath, "\n");
            Environment.SetEnvironmentVariable(fileName, emptyPath);
            Check.Throws<InvalidDataException>(
                () => new SecureNetworkOptions().ApplyEnvironment(),
                "certificate password file cannot be empty after newline trimming");

            var nulPath = Path.Combine(root, "nul-password");
            File.WriteAllText(nulPath, "secret\0suffix");
            Environment.SetEnvironmentVariable(fileName, nulPath);
            Check.Throws<InvalidDataException>(
                () => new SecureNetworkOptions().ApplyEnvironment(),
                "certificate password file rejects NUL characters");

            var invalidUtf8Path = Path.Combine(root, "invalid-utf8");
            File.WriteAllBytes(invalidUtf8Path, [0xC3, 0x28]);
            Environment.SetEnvironmentVariable(fileName, invalidUtf8Path);
            Check.Throws<InvalidDataException>(
                () => new SecureNetworkOptions().ApplyEnvironment(),
                "certificate password file rejects invalid UTF-8");

            var oversizedPath = Path.Combine(root, "oversized-password");
            File.WriteAllBytes(
                oversizedPath,
                Enumerable.Repeat((byte)'x', 4_099).ToArray());
            Environment.SetEnvironmentVariable(fileName, oversizedPath);
            Check.Throws<InvalidDataException>(
                () => new SecureNetworkOptions().ApplyEnvironment(),
                "certificate password file has a finite byte limit");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                directName,
                previousDirect);
            Environment.SetEnvironmentVariable(
                fileName,
                previousFile);
            Directory.Delete(root, recursive: true);
        }
    }
}
