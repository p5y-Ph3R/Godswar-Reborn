using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Godswar.Server.Operations;

namespace Godswar.Server.ProtocolChecks;

internal static class ControlledHostShutdownControlChecks
{
    internal static async Task RunAsync()
    {
        CheckActivationGuards();
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        await CheckAcceptedRequestAsync();
        await CheckMalformedClientsDoNotStopAsync();
        await CheckRequestDeadlineAsync();
        await CheckExternalCancellationAsync();
        await CheckSingleRunAsync();
    }

    private static void CheckActivationGuards()
    {
        using var shutdown = new CancellationTokenSource();
        var options = EligibleOptions();

        foreach (var value in new string?[]
        {
            null,
            string.Empty,
            "false",
            "True",
            "TRUE",
            "1",
            " true"
        })
        {
            Check.True(
                ControlledHostShutdownControl.TryCreate(
                    options,
                    controlledHostEvidenceActive: true,
                    shutdown,
                    value) is null,
                $"opt-in value '{value ?? "<null>"}' is disabled");
        }

        if (OperatingSystem.IsWindows())
        {
            Check.True(
                ControlledHostShutdownControl.TryCreate(
                    options,
                    controlledHostEvidenceActive: true,
                    shutdown,
                    "true") is not null,
                "exact opt-in enables the eligible control");
        }
        else
        {
            Check.Throws<PlatformNotSupportedException>(
                () => ControlledHostShutdownControl.TryCreate(
                    options,
                    controlledHostEvidenceActive: true,
                    shutdown,
                    "true"),
                "opted-in control fails closed off Windows");
        }

        Reject(
            shutdown,
            controlledHostEvidenceActive: false,
            description: "privacy evidence is mandatory");
        Reject(
            shutdown,
            mutate: value => value.Secure.Enabled = false,
            description: "TLS is mandatory");
        Reject(
            shutdown,
            mutate: value => value.Secure.Udp.Enabled = false,
            description: "UDP is mandatory");
        Reject(
            shutdown,
            mutate: value =>
                value.Secure.Login.BindHost = "localhost",
            description: "login bind must be exact");
        Reject(
            shutdown,
            mutate: value =>
                value.Secure.Game.BindHost = "::1",
            description: "game bind must be exact");
        Reject(
            shutdown,
            mutate: value =>
                value.Secure.Udp.BindHost = "127.0.0.2",
            description: "UDP bind must be exact");
    }

    private static async Task CheckAcceptedRequestAsync()
    {
        using var shutdown = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        var pipeName = NewPipeName();
        var control =
            ControlledHostShutdownControl.CreateForChecks(
                shutdown,
                pipeName,
                TimeSpan.FromMilliseconds(250));
        var runTask = control.RunAsync(lifetime.Token);

        var acknowledgement = await ExchangeAsync(
            pipeName,
            Encoding.ASCII.GetBytes(
                ControlledHostShutdownControl.RequestText),
            ControlledHostShutdownControl
                .AcknowledgementText.Length,
            lifetime.Token);
        await runTask.WaitAsync(lifetime.Token);

        Check.Equal(
            ControlledHostShutdownControl.AcknowledgementText,
            Encoding.ASCII.GetString(acknowledgement),
            "accepted request acknowledgement");
        Check.True(
            shutdown.IsCancellationRequested,
            "accepted request cancels the server shutdown source");
    }

    private static async Task CheckMalformedClientsDoNotStopAsync()
    {
        using var shutdown = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        var pipeName = NewPipeName();
        var control =
            ControlledHostShutdownControl.CreateForChecks(
                shutdown,
                pipeName,
                TimeSpan.FromMilliseconds(250));
        var runTask = control.RunAsync(lifetime.Token);

        await ConnectAndWriteAsync(
            pipeName,
            [],
            lifetime.Token);
        var malformed = Encoding.ASCII.GetBytes(
            ControlledHostShutdownControl.RequestText);
        malformed[0] ^= 0x20;
        await ConnectAndWriteAsync(
            pipeName,
            malformed,
            lifetime.Token);
        await ConnectAndWriteAsync(
            pipeName,
            Encoding.ASCII.GetBytes("REBORN"),
            lifetime.Token);
        var oversized = Encoding.ASCII.GetBytes(
            ControlledHostShutdownControl.RequestText + "X");
        await ConnectAndWriteAsync(
            pipeName,
            oversized,
            lifetime.Token);
        Check.True(
            !shutdown.IsCancellationRequested,
            "disconnected, malformed, undersized, and oversized " +
            "clients do not stop the server");

        var acknowledgement = await ExchangeAsync(
            pipeName,
            Encoding.ASCII.GetBytes(
                ControlledHostShutdownControl.RequestText),
            ControlledHostShutdownControl
                .AcknowledgementText.Length,
            lifetime.Token);
        await runTask.WaitAsync(lifetime.Token);

        Check.Equal(
            ControlledHostShutdownControl.AcknowledgementText,
            Encoding.ASCII.GetString(acknowledgement),
            "valid request remains available after malformed clients");
    }

    private static async Task CheckRequestDeadlineAsync()
    {
        using var shutdown = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        var pipeName = NewPipeName();
        var deadline = TimeSpan.FromMilliseconds(150);
        var control =
            ControlledHostShutdownControl.CreateForChecks(
                shutdown,
                pipeName,
                deadline);
        var runTask = control.RunAsync(lifetime.Token);

        await using (var client = await ConnectAsync(
            pipeName,
            lifetime.Token))
        {
            var timer = Stopwatch.StartNew();
            var buffer = new byte[1];
            var read = await client.ReadAsync(
                buffer,
                lifetime.Token);
            timer.Stop();
            Check.Equal(
                0,
                read,
                "silent request is disconnected without an ACK");
            Check.True(
                timer.Elapsed < TimeSpan.FromSeconds(2),
                "silent request has a bounded deadline");
        }

        Check.True(
            !shutdown.IsCancellationRequested,
            "silent request does not stop the server");
        _ = await ExchangeAsync(
            pipeName,
            Encoding.ASCII.GetBytes(
                ControlledHostShutdownControl.RequestText),
            ControlledHostShutdownControl
                .AcknowledgementText.Length,
            lifetime.Token);
        await runTask.WaitAsync(lifetime.Token);
    }

    private static async Task CheckExternalCancellationAsync()
    {
        using var shutdown = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        var control =
            ControlledHostShutdownControl.CreateForChecks(
                shutdown,
                NewPipeName(),
                TimeSpan.FromMilliseconds(250));
        var runTask = control.RunAsync(lifetime.Token);

        lifetime.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
        Check.True(
            !shutdown.IsCancellationRequested,
            "external lifetime cancellation is not a stop request");
    }

    private static async Task CheckSingleRunAsync()
    {
        using var shutdown = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        var control =
            ControlledHostShutdownControl.CreateForChecks(
                shutdown,
                NewPipeName(),
                TimeSpan.FromMilliseconds(250));
        var first = control.RunAsync(lifetime.Token);
        var second = control.RunAsync(lifetime.Token);
        await CheckThrowsAsync<InvalidOperationException>(
            second,
            "a control instance cannot be run twice");
        lifetime.Cancel();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task<byte[]> ExchangeAsync(
        string pipeName,
        byte[] request,
        int responseBytes,
        CancellationToken cancellationToken)
    {
        await using var client = await ConnectAsync(
            pipeName,
            cancellationToken);
        await client.WriteAsync(request, cancellationToken);
        await client.FlushAsync(cancellationToken);

        var response = new byte[responseBytes];
        var offset = 0;
        while (offset < response.Length)
        {
            var read = await client.ReadAsync(
                response.AsMemory(offset),
                cancellationToken);
            if (read == 0)
            {
                throw new InvalidOperationException(
                    "The control closed before the fixed ACK was read.");
            }
            offset += read;
        }
        return response;
    }

    private static async Task ConnectAndWriteAsync(
        string pipeName,
        byte[] request,
        CancellationToken cancellationToken)
    {
        await using var client = await ConnectAsync(
            pipeName,
            cancellationToken);
        if (request.Length > 0)
        {
            await client.WriteAsync(request, cancellationToken);
            await client.FlushAsync(cancellationToken);
        }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static async Task CheckThrowsAsync<TException>(
        Task task,
        string description)
        where TException : Exception
    {
        try
        {
            await task;
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected " +
            $"{typeof(TException).Name}.");
    }

    private static void Reject(
        CancellationTokenSource shutdown,
        bool controlledHostEvidenceActive = true,
        Action<ServerOptions>? mutate = null,
        string description = "invalid activation")
    {
        var options = EligibleOptions();
        mutate?.Invoke(options);
        Check.Throws<InvalidOperationException>(
            () => ControlledHostShutdownControl.TryCreate(
                options,
                controlledHostEvidenceActive,
                shutdown,
                "true"),
            description);
    }

    private static ServerOptions EligibleOptions()
    {
        var options = new ServerOptions();
        options.Secure.Enabled = true;
        options.Secure.Udp.Enabled = true;
        options.Secure.Login.BindHost = "127.0.0.1";
        options.Secure.Game.BindHost = "127.0.0.1";
        options.Secure.Udp.BindHost = "127.0.0.1";
        return options;
    }

    private static string NewPipeName() =>
        $"reborn-phase4-stop-check-{Guid.NewGuid():N}";
}
