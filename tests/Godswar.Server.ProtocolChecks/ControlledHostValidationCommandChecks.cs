using Godswar.Server.Operations;

namespace Godswar.Server.ProtocolChecks;

internal static class ControlledHostValidationCommandChecks
{
    private const string Database =
        "godswar_secure_acceptance_20260726_141154";

    internal static Task RunAsync()
    {
        ControlledHostValidationCommand.ValidateDatabaseScope(
            Valid("127.0.0.1"),
            Database);
        ControlledHostValidationCommand.ValidateDatabaseScope(
            Valid("::1"),
            Database);

        Reject(
            $"Host=127.0.0.1;Server=192.0.2.1;Database={Database};" +
            "Username=test;Password=fake",
            "Npgsql Server alias cannot override loopback");
        Reject(
            $"Host=127.0.0.1;Database={Database};DB=godswar;" +
            "Username=test;Password=fake",
            "Npgsql DB alias cannot override the acceptance database");
        Reject(
            $"Host=127.0.0.1;Host=192.0.2.1;Database={Database};" +
            "Username=test;Password=fake",
            "duplicate host cannot override loopback");
        Reject(
            $"Host=localhost;Database={Database};Username=test;Password=fake",
            "DNS host is not literal loopback");
        Reject(
            $"Host=127.0.0.1;Port=5433;Database={Database};" +
            "Username=test;Password=fake",
            "nonstandard local port is outside acceptance scope");
        Reject(
            "Host=127.0.0.1;Database=godswar;" +
            "Username=test;Password=fake",
            "live database name is outside acceptance scope");
        Reject(
            $"Database={Database};Username=test;Password=fake",
            "missing host is rejected");
        Reject(
            "not a connection string",
            "malformed Npgsql connection string is rejected");
        return Task.CompletedTask;
    }

    private static string Valid(string host) =>
        $"Host={host};Port=5432;Database={Database};" +
        "Username=test;Password=fake;Pooling=true";

    private static void Reject(
        string connectionString,
        string description) =>
        Check.Throws<Exception>(
            () => ControlledHostValidationCommand.ValidateDatabaseScope(
                connectionString,
                Database),
            description);
}
