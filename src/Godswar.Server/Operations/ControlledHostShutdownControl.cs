using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;

namespace Godswar.Server.Operations;

internal sealed class ControlledHostShutdownControl
{
    internal const string EnabledEnvironmentVariable =
        "GODSWAR_CONTROLLED_HOST_SHUTDOWN_ENABLED";

    internal const string PipeName =
        "reborn-phase4-controlled-host-shutdown-v1";

    internal const string RequestText =
        "REBORN_PHASE4_STOP_V1\n";

    internal const string AcknowledgementText =
        "REBORN_PHASE4_STOP_ACCEPTED_V1\n";

    private static readonly byte[] RequestBytes =
        Encoding.ASCII.GetBytes(RequestText);

    private static readonly byte[] AcknowledgementBytes =
        Encoding.ASCII.GetBytes(AcknowledgementText);

    private static readonly TimeSpan DefaultRequestDeadline =
        TimeSpan.FromSeconds(2);

    private readonly string _pipeName;
    private readonly TimeSpan _requestDeadline;
    private readonly CancellationTokenSource _shutdown;
    private int _started;

    private ControlledHostShutdownControl(
        CancellationTokenSource shutdown,
        string pipeName,
        TimeSpan requestDeadline)
    {
        _shutdown = shutdown ??
            throw new ArgumentNullException(nameof(shutdown));
        if (string.IsNullOrWhiteSpace(pipeName) ||
            pipeName.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(pipeName));
        }
        if (requestDeadline < TimeSpan.FromMilliseconds(50) ||
            requestDeadline > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestDeadline));
        }

        _pipeName = pipeName;
        _requestDeadline = requestDeadline;
    }

    internal static ControlledHostShutdownControl?
        TryCreateFromEnvironment(
            ServerOptions options,
            bool controlledHostEvidenceActive,
            CancellationTokenSource shutdown) =>
        TryCreate(
            options,
            controlledHostEvidenceActive,
            shutdown,
            Environment.GetEnvironmentVariable(
                EnabledEnvironmentVariable));

    internal static ControlledHostShutdownControl? TryCreate(
        ServerOptions options,
        bool controlledHostEvidenceActive,
        CancellationTokenSource shutdown,
        string? optIn)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(shutdown);

        if (!string.Equals(optIn, "true", StringComparison.Ordinal))
        {
            return null;
        }

        if (!controlledHostEvidenceActive ||
            !options.Secure.Enabled ||
            !options.Secure.Udp.Enabled ||
            !IsExactLoopback(options.Secure.Login.BindHost) ||
            !IsExactLoopback(options.Secure.Game.BindHost) ||
            !IsExactLoopback(options.Secure.Udp.BindHost))
        {
            throw new InvalidOperationException(
                "The controlled-host shutdown pipe requires active " +
                "privacy evidence, secure TLS and UDP, and exact " +
                "127.0.0.1 login, game, and UDP binds.");
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The controlled-host same-user shutdown pipe is " +
                "available only on Windows.");
        }

        return new ControlledHostShutdownControl(
            shutdown,
            PipeName,
            DefaultRequestDeadline);
    }

    internal static ControlledHostShutdownControl CreateForChecks(
        CancellationTokenSource shutdown,
        string pipeName,
        TimeSpan requestDeadline) =>
        new(shutdown, pipeName, requestDeadline);

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The controlled-host same-user shutdown pipe is " +
                "available only on Windows.");
        }
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                "The controlled-host shutdown control can run only once.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = CreatePipe();
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                if (await IsAcceptedRequestAsync(
                        pipe,
                        cancellationToken))
                {
                    await WriteAcknowledgementAsync(
                        pipe,
                        cancellationToken);
                    _shutdown.Cancel();
                    return;
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // A same-user client did not complete the fixed request or
                // acknowledgement inside the per-connection deadline.
            }
            catch (IOException)
            {
                // A disconnected or malformed client must not disable the
                // one-instance control for the next bounded request.
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreatePipe() =>
        new(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: RequestBytes.Length,
            outBufferSize: AcknowledgementBytes.Length);

    private async Task<bool> IsAcceptedRequestAsync(
        NamedPipeServerStream stream,
        CancellationToken cancellationToken)
    {
        var request = new byte[RequestBytes.Length + 1];
        using var deadline =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        deadline.CancelAfter(_requestDeadline);

        var offset = 0;
        while (true)
        {
            var read = await stream.ReadAsync(
                request.AsMemory(offset),
                deadline.Token);
            if (read == 0)
            {
                return false;
            }
            offset += read;
            if (offset > RequestBytes.Length)
            {
                return false;
            }
            if (stream.IsMessageComplete)
            {
                break;
            }
        }

        return offset == RequestBytes.Length &&
            request.AsSpan(0, offset).SequenceEqual(RequestBytes);
    }

    private async Task WriteAcknowledgementAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var deadline =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        deadline.CancelAfter(_requestDeadline);
        await stream.WriteAsync(
            AcknowledgementBytes,
            deadline.Token);
        await stream.FlushAsync(deadline.Token);
    }

    private static bool IsExactLoopback(string? value) =>
        string.Equals(
            value,
            "127.0.0.1",
            StringComparison.Ordinal);
}
