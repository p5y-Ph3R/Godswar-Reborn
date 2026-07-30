using System.Runtime.InteropServices;
using Godswar.Server.Networking;

namespace Godswar.Server.Operations;

internal sealed class ServerDrainCoordinator
{
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(100);

    private readonly ConnectionAdmission _admission;
    private readonly TimeSpan _drainTimeout;
    private readonly CancellationTokenSource _shutdown;
    private readonly ServerOperationalState _state;
    private readonly object _sync = new();
    private Task? _completion;

    public ServerDrainCoordinator(
        ServerOperationalState state,
        ConnectionAdmission admission,
        CancellationTokenSource shutdown,
        TimeSpan drainTimeout)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _admission = admission ?? throw new ArgumentNullException(
            nameof(admission));
        _shutdown = shutdown ?? throw new ArgumentNullException(
            nameof(shutdown));
        if (drainTimeout < TimeSpan.FromMilliseconds(1) ||
            drainTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(drainTimeout));
        }
        _drainTimeout = drainTimeout;
    }

    public ManagementDrainResult BeginDrain()
    {
        lock (_sync)
        {
            if (_completion is not null)
            {
                return ManagementDrainResult.AlreadyDraining;
            }
            if (!_state.TryBeginDrain())
            {
                return ManagementDrainResult.Rejected;
            }

            _admission.BeginDrain();
            _completion = DrainThenStopAsync();
            return ManagementDrainResult.Accepted;
        }
    }

    public Task GetCompletion()
    {
        lock (_sync)
        {
            return _completion ?? Task.CompletedTask;
        }
    }

    private async Task DrainThenStopAsync()
    {
        var started = TimeProvider.System.GetTimestamp();
        try
        {
            var responseGrace = TimeSpan.FromTicks(Math.Min(
                TimeSpan.FromSeconds(1).Ticks,
                Math.Max(1, _drainTimeout.Ticks / 4)));
            await Task.Delay(responseGrace);
            while (_admission.GetSnapshot().ActiveConnections > 0)
            {
                var elapsed =
                    TimeProvider.System.GetElapsedTime(started);
                var remaining = _drainTimeout - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }
                await Task.Delay(
                    remaining < PollInterval
                        ? remaining
                        : PollInterval);
            }
        }
        finally
        {
            _state.TryMarkStopping();
            try
            {
                _shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

internal sealed class ServerProcessSignalRegistration : IDisposable
{
    private readonly PosixSignalRegistration? _registration;

    private ServerProcessSignalRegistration(
        PosixSignalRegistration? registration)
    {
        _registration = registration;
    }

    public static ServerProcessSignalRegistration Install(
        Action requestDrain)
    {
        ArgumentNullException.ThrowIfNull(requestDrain);
        PosixSignalRegistration? registration = null;
        if (!OperatingSystem.IsWindows())
        {
            registration = PosixSignalRegistration.Create(
                PosixSignal.SIGTERM,
                context =>
                {
                    context.Cancel = true;
                    requestDrain();
                });
        }

        return new ServerProcessSignalRegistration(registration);
    }

    public void Dispose() => _registration?.Dispose();
}
