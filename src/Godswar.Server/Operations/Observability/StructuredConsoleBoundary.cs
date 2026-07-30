using System.Text;

namespace Godswar.Server.Operations.Observability;

internal enum LegacyConsoleSource : byte
{
    Stdout = 1,
    Stderr = 2
}

internal sealed class StructuredConsoleBoundary : IDisposable
{
    private readonly TextWriter _originalError;
    private readonly TextWriter _originalOutput;
    private readonly TextWriter _installedError;
    private readonly TextWriter _installedOutput;
    private readonly BoundedLegacyConsoleWriter _stderr;
    private readonly BoundedLegacyConsoleWriter _stdout;
    private int _disposed;

    private StructuredConsoleBoundary(
        TextWriter originalOutput,
        TextWriter originalError,
        TextWriter installedOutput,
        TextWriter installedError,
        BoundedLegacyConsoleWriter stdout,
        BoundedLegacyConsoleWriter stderr)
    {
        _originalOutput = originalOutput;
        _originalError = originalError;
        _installedOutput = installedOutput;
        _installedError = installedError;
        _stdout = stdout;
        _stderr = stderr;
    }

    public static StructuredConsoleBoundary Install(
        BoundedStructuredLogger logger,
        StructuredLogOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var effectiveOptions = options ?? new StructuredLogOptions();
        effectiveOptions.Validate();

        var originalOutput = Console.Out;
        var originalError = Console.Error;
        var stdout = new BoundedLegacyConsoleWriter(
            logger,
            LegacyConsoleSource.Stdout,
            effectiveOptions.MaximumLegacyCharactersPerWrite);
        var stderr = new BoundedLegacyConsoleWriter(
            logger,
            LegacyConsoleSource.Stderr,
            effectiveOptions.MaximumLegacyCharactersPerWrite);
        Console.SetOut(stdout);
        Console.SetError(stderr);
        return new StructuredConsoleBoundary(
            originalOutput,
            originalError,
            Console.Out,
            Console.Error,
            stdout,
            stderr);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stdout.Dispose();
        _stderr.Dispose();
        if (ReferenceEquals(Console.Out, _installedOutput))
        {
            Console.SetOut(_originalOutput);
        }
        if (ReferenceEquals(Console.Error, _installedError))
        {
            Console.SetError(_originalError);
        }
    }
}

internal sealed class BoundedLegacyConsoleWriter : TextWriter
{
    private const int MaximumPrefixCharacters = 64;

    private readonly long _maximumCharactersPerWrite;
    private readonly BoundedStructuredLogger _logger;
    private readonly char[] _prefix = new char[MaximumPrefixCharacters];
    private readonly LegacyConsoleSource _source;
    private readonly object _gate = new();

    private long _characterCount;
    private int _disposed;
    private bool _lineOpen;
    private int _prefixLength;
    private bool _skipLineFeed;
    private bool _truncated;

    public BoundedLegacyConsoleWriter(
        BoundedStructuredLogger logger,
        LegacyConsoleSource source,
        int maximumCharactersPerWrite)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maximumCharactersPerWrite,
            MaximumPrefixCharacters);
        _source = source;
        _maximumCharactersPerWrite = maximumCharactersPerWrite;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_gate)
        {
            if (_disposed == 0)
            {
                ProcessCharacterLocked(value);
            }
        }
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        Write(value.AsSpan());
    }

    public override void Write(
        char[] buffer,
        int index,
        int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (index > buffer.Length - count)
        {
            throw new ArgumentException(
                "The character slice is outside the source buffer.");
        }

        Write(buffer.AsSpan(index, count));
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        lock (_gate)
        {
            if (_disposed != 0 || buffer.IsEmpty)
            {
                return;
            }
            if (buffer.Length > _maximumCharactersPerWrite)
            {
                CapturePrefixLocked(
                    buffer[..Math.Min(
                        buffer.Length,
                        MaximumPrefixCharacters)]);
                _characterCount = SaturatingAdd(
                    _characterCount,
                    buffer.Length);
                _lineOpen = true;
                _truncated = true;
                CompleteLineLocked();
                _skipLineFeed = false;
                return;
            }

            foreach (var value in buffer)
            {
                ProcessCharacterLocked(value);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                if (_disposed == 0)
                {
                    CompleteLineLocked();
                    _disposed = 1;
                }
            }
        }

        base.Dispose(disposing);
    }

    private void ProcessCharacterLocked(char value)
    {
        if (value == '\r')
        {
            CompleteLineLocked();
            _skipLineFeed = true;
            return;
        }
        if (value == '\n')
        {
            if (_skipLineFeed)
            {
                _skipLineFeed = false;
                return;
            }
            CompleteLineLocked();
            return;
        }

        _skipLineFeed = false;
        _lineOpen = true;
        _characterCount = SaturatingAdd(_characterCount, 1);
        if (_prefixLength < _prefix.Length)
        {
            _prefix[_prefixLength++] = value;
        }
        if (_characterCount > _maximumCharactersPerWrite)
        {
            _truncated = true;
        }
    }

    private void CapturePrefixLocked(ReadOnlySpan<char> value)
    {
        var available = _prefix.Length - _prefixLength;
        var copied = Math.Min(value.Length, available);
        value[..copied].CopyTo(_prefix.AsSpan(_prefixLength));
        _prefixLength += copied;
    }

    private void CompleteLineLocked()
    {
        if (!_lineOpen)
        {
            return;
        }

        _logger.SuppressLegacyLine(
            _source,
            _prefix.AsSpan(0, _prefixLength),
            _characterCount,
            _truncated);
        _characterCount = 0;
        _lineOpen = false;
        _prefixLength = 0;
        _truncated = false;
        Array.Clear(_prefix);
    }

    private static long SaturatingAdd(long value, long addition) =>
        value > long.MaxValue - addition
            ? long.MaxValue
            : value + addition;
}

internal static class LegacyDiagnosticClassifier
{
    private static readonly (string Prefix, string Code)[] Known =
    [
        ("[startup]", "startup"),
        ("[security]", "security"),
        ("[db]", "database"),
        ("[net]", "network"),
        ("[game]", "game"),
        ("[world]", "world"),
        ("[map]", "map"),
        ("[npc]", "npc"),
        ("[mob]", "monster"),
        ("[monster]", "monster"),
        ("[attack]", "combat"),
        ("[skill]", "combat"),
        ("[reward]", "reward"),
        ("[progression]", "progression"),
        ("[zodiac]", "progression"),
        ("[pet]", "pet"),
        ("[inventory]", "inventory"),
        ("[forge]", "inventory"),
        ("[status]", "status"),
        ("[recovery]", "recovery")
    ];

    public static string Classify(ReadOnlySpan<char> prefix)
    {
        foreach (var known in Known)
        {
            if (prefix.StartsWith(
                    known.Prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return known.Code;
            }
        }

        return "other";
    }
}
