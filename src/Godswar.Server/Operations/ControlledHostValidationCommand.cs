using System.Net;
using System.Text.RegularExpressions;
using Npgsql;

namespace Godswar.Server.Operations;

internal static partial class ControlledHostValidationCommand
{
    internal const string DatabaseScopeMode =
        "--controlled-host-validate-database-scope";
    internal const string OptionsMode =
        "--controlled-host-validate-options";
    internal const string CertificateMode =
        "--controlled-host-validate-certificate";
    internal const string DatabaseScopeAccepted =
        "CONTROLLED_HOST_DATABASE_SCOPE_VALID";
    internal const string OptionsAccepted =
        "CONTROLLED_HOST_OPTIONS_VALID";
    internal const string CertificateAccepted =
        "CONTROLLED_HOST_CERTIFICATE_VALID";
    internal const int MaximumConnectionStringCharacters = 4_096;

    internal static async Task<bool> TryRunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 ||
            (args[0] != DatabaseScopeMode &&
             args[0] != OptionsMode &&
             args[0] != CertificateMode))
        {
            return false;
        }

        Environment.ExitCode = 2;
        try
        {
            if (args[0] == DatabaseScopeMode)
            {
                if (args.Length != 2 ||
                    !AcceptanceDatabaseName().IsMatch(args[1]))
                {
                    return true;
                }

                var connectionString =
                    NormalizeStandardInput(
                        await ReadBoundedStandardInputAsync());
                ValidateDatabaseScope(connectionString, args[1]);
                Console.Out.WriteLine(DatabaseScopeAccepted);
                Environment.ExitCode = 0;
                return true;
            }

            if (args[0] == CertificateMode)
            {
                if (args.Length != 4 ||
                    !AreExistingFullyQualifiedFiles(args[1..]))
                {
                    return true;
                }

                var password =
                    NormalizeStandardInput(
                        await ReadBoundedStandardInputAsync());
                ValidateCertificateBundle(
                    args[1],
                    args[2],
                    args[3],
                    password);
                Console.Out.WriteLine(CertificateAccepted);
                Environment.ExitCode = 0;
                return true;
            }

            if (args.Length != 4 ||
                !Path.IsPathFullyQualified(args[1]) ||
                !File.Exists(args[1]) ||
                !bool.TryParse(args[3], out var expectedFaults))
            {
                return true;
            }

            var expectedCertificate = Path.GetFullPath(args[2]);
            var options = ServerOptions.Load(args[1]);
            ValidateAcceptanceOptions(
                options,
                expectedCertificate,
                expectedFaults);
            Console.Out.WriteLine(OptionsAccepted);
            Environment.ExitCode = 0;
            return true;
        }
        catch
        {
            Console.Error.WriteLine(
                "Controlled-host validation was rejected.");
            return true;
        }
    }

    private static bool AreExistingFullyQualifiedFiles(
        IEnumerable<string> paths) =>
        paths.All(static path =>
            Path.IsPathFullyQualified(path) &&
            File.Exists(path));

    private static string NormalizeStandardInput(string value)
    {
        if (value.Length > 0 && value[0] == '\uFEFF')
        {
            value = value[1..];
        }
        // Windows PowerShell 5.1's redirected StreamWriter can surface its
        // UTF-8 BOM through OEM 437 as these fixed three characters.
        const string windowsPowerShellPreamble = "\u2229\u2557\u2510";
        if (value.StartsWith(
                windowsPowerShellPreamble,
                StringComparison.Ordinal))
        {
            value = value[windowsPowerShellPreamble.Length..];
        }
        return value;
    }

    internal static void ValidateDatabaseScope(
        string connectionString,
        string expectedDatabaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (!AcceptanceDatabaseName().IsMatch(expectedDatabaseName))
        {
            throw new ArgumentException(
                "The expected database name is outside acceptance scope.",
                nameof(expectedDatabaseName));
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var host = builder.Host?.Trim();
        var database = builder.Database?.Trim();
        if (string.IsNullOrWhiteSpace(host) ||
            string.IsNullOrWhiteSpace(database) ||
            !IPAddress.TryParse(host, out var address) ||
            !IPAddress.IsLoopback(address) ||
            !string.Equals(
                database,
                expectedDatabaseName,
                StringComparison.Ordinal) ||
            builder.Port != 5_432)
        {
            throw new InvalidDataException(
                "The PostgreSQL connection is outside acceptance scope.");
        }
    }

    private static async Task<string> ReadBoundedStandardInputAsync()
    {
        var buffer =
            new char[MaximumConnectionStringCharacters + 1];
        var count = 0;
        try
        {
            while (count < buffer.Length)
            {
                var read = await Console.In.ReadAsync(
                    buffer.AsMemory(count, buffer.Length - count));
                if (read == 0)
                {
                    break;
                }
                count += read;
            }
            if (count > MaximumConnectionStringCharacters)
            {
                throw new InvalidDataException(
                    "Controlled-host standard input is oversized.");
            }
            return new string(buffer, 0, count);
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    [GeneratedRegex(
        "^godswar_secure_acceptance_[0-9]{8}_[0-9]{6}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AcceptanceDatabaseName();
}
