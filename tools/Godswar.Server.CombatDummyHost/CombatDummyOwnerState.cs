using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Godswar.Server.CombatDummyHost;

internal static class CombatDummyOwnerState
{
    public static void Publish(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException(
                "The owner-state path has no parent directory.");
        var executable = Environment.ProcessPath ??
            throw new InvalidOperationException(
                "The host executable path is unavailable.");
        using var process = Process.GetCurrentProcess();
        var state = new OwnerState(
            ProcessId: Environment.ProcessId,
            ProcessStartTimeUtcTicks:
                process.StartTime.ToUniversalTime().Ticks,
            Executable: Path.GetFullPath(executable),
            IdentityManifest: CombatDummyDefinition.IdentityManifest,
            StartedAtUtc: DateTimeOffset.UtcNow);
        Directory.CreateDirectory(directory);
        var temporary = fullPath + $".tmp.{Environment.ProcessId}";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(
                state,
                new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, fullPath, overwrite: true);
    }

    private sealed record OwnerState(
        int ProcessId,
        long ProcessStartTimeUtcTicks,
        string Executable,
        string IdentityManifest,
        DateTimeOffset StartedAtUtc);
}
