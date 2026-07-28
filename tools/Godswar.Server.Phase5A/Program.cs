using System.Text.Json;

namespace Godswar.Server.Phase5A;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    public static async Task<int> Main(string[] args)
    {
        if (args.SequenceEqual(["--help"], StringComparer.Ordinal))
        {
            PrintHelp();
            return 0;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };

        try
        {
            object report;
            if (args.SequenceEqual(
                    ["--self-check"],
                    StringComparer.Ordinal))
            {
                stop.CancelAfter(TimeSpan.FromSeconds(15));
                report = await Phase5ASelfCheck.RunAsync(stop.Token);
            }
            else
            {
                var options = Phase5AOptions.Parse(args);
                stop.CancelAfter(GetRuntimeDeadline(options));
                report = await new MovementLoadRunner().RunAsync(
                    options,
                    stop.Token);
            }

            Console.WriteLine(
                JsonSerializer.Serialize(report, JsonOptions));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(
                "{\"result\":\"failed\",\"error\":\"bounded runtime deadline or cancellation\"}");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        result = "failed",
                        error = SafeError(error)
                    },
                    JsonOptions));
            return 1;
        }
    }

    private static TimeSpan GetRuntimeDeadline(
        Phase5AOptions options) =>
        options.Mode == Phase5AMode.PacedSoak
            ? TimeSpan.FromSeconds(
                options.DurationSeconds + 15)
            : TimeSpan.FromSeconds(60);

    private static string SafeError(Exception error)
    {
        var message = error.Message
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        return message.Length <= 240
            ? message
            : message[..240];
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            Godswar.Server.Phase5A - bounded, in-process movement load baseline

              --mode load|paced-soak       default: load
              --bots 1..512                default: 64
              --duration-seconds 1..300    default: 10
              --tick-rate 20               fixed authoritative rate
              --seed 1..4294967295         deterministic seed
              --self-check                 run bounded validation checks

            Reports are JSON on stdout. This tool opens no sockets and accepts
            no network target. The default is 76,800 operations. Bot and
            duration limits are individual caps; their combination must also
            remain at or below the 5,000,000-operation hard cap.
            """);
    }
}
