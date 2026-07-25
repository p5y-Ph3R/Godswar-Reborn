using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure;

internal sealed partial class TlsMuxLegacyTransport
{
    private static readonly TimeSpan HeartbeatPollInterval =
        TimeSpan.FromSeconds(1);

    private static readonly TimeSpan HeartbeatSendIdle =
        TimeSpan.FromSeconds(SecureProtocolConstants.HeartbeatSeconds);

    private static readonly TimeSpan HeartbeatReceiveIdle =
        TimeSpan.FromSeconds(SecureProtocolConstants.IdleTimeoutSeconds);

    private static readonly TimeSpan PongDeadline =
        TimeSpan.FromSeconds(10);

    private async Task RunControlAsync()
    {
        try
        {
            while (true)
            {
                var result = await _controlQueue.DequeueAsync(
                    _lifetime.Token);
                if (!result.HasItem)
                {
                    return;
                }

                SecureNetworkMetrics.ControlRemoved(
                    _endpointRole,
                    itemCount: 1,
                    result.ByteCount);
                var work = result.Item;
                try
                {
                    ValidatePong(work.Payload);
                    work.SetResult();
                }
                catch (Exception error)
                {
                    work.SetException(error);
                    throw;
                }
                finally
                {
                    work.Clear();
                }
            }
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Fail(error);
        }
        finally
        {
            var drained = _controlQueue.TryDrain();
            if (drained.Count > 0)
            {
                SecureNetworkMetrics.ControlRemoved(
                    _endpointRole,
                    drained.Count,
                    drained.Sum(static entry => (long)entry.ByteCount));
            }

            var terminal = new SecureTransportException(
                "The secure control channel stopped.");
            foreach (var entry in drained)
            {
                entry.Item.SetException(terminal);
                entry.Item.Clear();
            }
        }
    }

    private async Task RunHeartbeatAsync()
    {
        try
        {
            await _authenticated.Task.WaitAsync(_lifetime.Token);
            while (!_lifetime.IsCancellationRequested)
            {
                var sendPing = false;
                lock (_heartbeatGate)
                {
                    var now = _timeProvider.GetTimestamp();
                    if (_timeProvider.GetElapsedTime(
                            _lastReceiveTimestamp,
                            now) >= HeartbeatReceiveIdle)
                    {
                        throw new NetworkDeadlineException(
                            NetworkTimeoutStage.Idle);
                    }
                    if (_pingOutstanding &&
                        _timeProvider.GetElapsedTime(
                            _pingTimestamp,
                            now) >= PongDeadline)
                    {
                        throw new NetworkDeadlineException(
                            NetworkTimeoutStage.Idle);
                    }
                    sendPing = !_pingOutstanding &&
                        _timeProvider.GetElapsedTime(
                            _lastSendTimestamp,
                            now) >= HeartbeatSendIdle;
                }

                if (sendPing)
                {
                    await SendPingAsync(_lifetime.Token);
                }

                await Task.Delay(
                    HeartbeatPollInterval,
                    _timeProvider,
                    _lifetime.Token);
            }
        }
        catch (NetworkDeadlineException error)
        {
            NetworkRuntimeMetrics.RecordTimeout(_endpointRole, error.Stage);
            Fail(error);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private async Task SendPingAsync(CancellationToken cancellationToken)
    {
        var nonce = new byte[8];
        FillNonzeroNonce(nonce);
        lock (_heartbeatGate)
        {
            if (_pingOutstanding)
            {
                return;
            }

            nonce.CopyTo(_pingNonce, 0);
            _pingTimestamp = _timeProvider.GetTimestamp();
            _pingOutstanding = true;
        }

        try
        {
            using var deadline = new CancellationTokenSource(
                _options.ReliableWriteTimeout,
                _timeProvider);
            using var writeLifetime =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    deadline.Token);
            try
            {
                await WriteControlFrameAsync(
                    SecureFrameType.Ping,
                    nonce,
                    writeLifetime.Token);
            }
            catch (OperationCanceledException)
                when (deadline.IsCancellationRequested)
            {
                throw new NetworkDeadlineException(
                    NetworkTimeoutStage.ReliableWrite);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private void ValidatePong(ReadOnlySpan<byte> payload)
    {
        lock (_heartbeatGate)
        {
            if (!_heartbeatActive ||
                !_pingOutstanding ||
                !CryptographicOperations.FixedTimeEquals(
                    payload,
                    _pingNonce))
            {
                throw new SecureTransportException(
                    "A secure Pong was unsolicited, duplicated, or did not match the outstanding Ping.");
            }

            _pingOutstanding = false;
            _pingTimestamp = 0;
            CryptographicOperations.ZeroMemory(_pingNonce);
            _lastReceiveTimestamp = _timeProvider.GetTimestamp();
        }
    }

    private void RecordValidReceivedFrame()
    {
        lock (_heartbeatGate)
        {
            if (_heartbeatActive)
            {
                _lastReceiveTimestamp = _timeProvider.GetTimestamp();
            }
        }
    }

    private void RecordValidSentFrame()
    {
        lock (_heartbeatGate)
        {
            if (_heartbeatActive)
            {
                _lastSendTimestamp = _timeProvider.GetTimestamp();
            }
        }
    }

    private static void FillNonzeroNonce(Span<byte> nonce)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            RandomNumberGenerator.Fill(nonce);
            if (!SecureProtocolValidation.IsAllZero(nonce))
            {
                return;
            }
        }

        throw new CryptographicException(
            "CSPRNG returned an invalid heartbeat nonce.");
    }
}
