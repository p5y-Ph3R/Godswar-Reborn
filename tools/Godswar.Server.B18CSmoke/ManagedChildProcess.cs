using System.Diagnostics;
using System.Text;

namespace Godswar.Server.B18CSmoke;

internal sealed class ManagedChildProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly BoundedLogTail _tail = new();
    private int _stopped;

    private ManagedChildProcess(string name, Process process)
    {
        Name = name;
        _process = process;
    }

    public int Id => _process.Id;

    public string Name { get; }

    public bool HasExited
    {
        get
        {
            _process.Refresh();
            return _process.HasExited;
        }
    }

    public static ManagedChildProcess Start(
        string name,
        string executablePath,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var key in start.Environment.Keys
                     .Where(static key =>
                         key.StartsWith(
                             "GODSWAR_",
                             StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            start.Environment.Remove(key);
        }
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                start.Environment[pair.Key] = pair.Value;
            }
        }

        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        var process = new Process
        {
            StartInfo = start,
            EnableRaisingEvents = true
        };
        var child = new ManagedChildProcess(name, process);
        process.OutputDataReceived += (_, eventArgs) =>
            child._tail.Add("stdout", eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) =>
            child._tail.Add("stderr", eventArgs.Data);
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Could not start the {name} process.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return child;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public void RequireRunning(string stage)
    {
        if (!HasExited)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{Name} exited with code {_process.ExitCode} during {stage}.");
    }

    public string RenderLogTail() => _tail.Render(Name);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        try
        {
            if (!HasExited)
            {
                TryKillTree();
            }

            using var timeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try
            {
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKillTree();
            }

            if (_process.HasExited)
            {
                // Flush asynchronous redirected-output callbacks.
                _process.WaitForExit();
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    private void TryKillTree()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private sealed class BoundedLogTail
    {
        private const int MaximumLines = 32;
        private const int MaximumLineCharacters = 320;
        private readonly Queue<string> _lines = new();
        private readonly object _sync = new();

        public void Add(string stream, string? line)
        {
            if (line is null)
            {
                return;
            }

            var normalized = line
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            if (normalized.Length > MaximumLineCharacters)
            {
                normalized = normalized[..MaximumLineCharacters];
            }

            lock (_sync)
            {
                while (_lines.Count >= MaximumLines)
                {
                    _lines.Dequeue();
                }

                _lines.Enqueue($"{stream}: {normalized}");
            }
        }

        public string Render(string processName)
        {
            lock (_sync)
            {
                if (_lines.Count == 0)
                {
                    return $"{processName} log tail: <empty>";
                }

                var builder = new StringBuilder(
                    $"{processName} log tail:");
                foreach (var line in _lines)
                {
                    builder.AppendLine();
                    builder.Append(line);
                }

                return builder.ToString();
            }
        }
    }
}
