namespace Godswar.Server.CombatDummyHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 1 &&
                string.Equals(
                    args[0],
                    "--print-identity-manifest",
                    StringComparison.Ordinal))
            {
                Console.WriteLine(CombatDummyDefinition.IdentityManifest);
                return 0;
            }
            if (args.Length == 1 &&
                string.Equals(
                    args[0],
                    "--self-test",
                    StringComparison.Ordinal))
            {
                CombatDummyHostSelfTest.Run();
                Console.WriteLine("Combat dummy host self-test passed.");
                return 0;
            }

            var options = CombatDummyHostOptions.Parse(args);
            Directory.CreateDirectory(
                Path.GetDirectoryName(options.SingletonFile) ??
                throw new InvalidOperationException(
                    "The singleton path has no parent directory."));
            await using var singleton = AcquireSingleton(
                options.SingletonFile);
            CombatDummyOwnerState.Publish(options.OwnerFile);
            var readiness = new CombatDummyReadiness(
                options.ReadinessFile);
            using var lifetime = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                lifetime.Cancel();
            };

            Console.WriteLine(
                $"Starting {CombatDummyDefinition.All.Count} development " +
                $"combat dummies at {options.Address}:{options.GamePort}.");
            var sessions = CombatDummyDefinition.All
                .Select(definition => new CombatDummyConnection(
                    definition,
                    options,
                    readiness).RunAsync(lifetime.Token))
                .ToArray();
            await Task.WhenAll(sessions);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"Combat dummy host failed: {error.GetType().Name}: " +
                error.Message.Replace('\r', ' ').Replace('\n', ' '));
            return 1;
        }
    }

    private static FileStream AcquireSingleton(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException(
                "Another development combat-dummy host owns the " +
                "singleton lock.",
                error);
        }
    }
}
