using System.Buffers.Binary;

sealed class CaptureLog : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    public CaptureLog(string outputPath)
    {
        _writer = new StreamWriter(File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public void Line(string message)
    {
        lock (_lock)
        {
            _writer.WriteLine($"{DateTimeOffset.Now:O} {message}");
        }
    }

    public void Chunk(string direction, ReadOnlySpan<byte> clearBytes, ReadOnlySpan<byte> rawBytes)
    {
        lock (_lock)
        {
            _writer.WriteLine($"{DateTimeOffset.Now:O} {direction} bytes={clearBytes.Length} clearHead={DescribeHead(clearBytes)} rawHead={DescribeHead(rawBytes)}");
            _writer.WriteLine("CLEAR " + Convert.ToHexString(clearBytes));
            _writer.WriteLine("RAW   " + Convert.ToHexString(rawBytes));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
    }

    private static string DescribeHead(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            return "short";
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[..2]);
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2, 2));
        return $"declared={length} opcode={opcode}";
    }
}
