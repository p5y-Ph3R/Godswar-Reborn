using System.Text;

namespace Godswar.Server.Operations;

internal enum ControlledHostEvidenceEvent : byte
{
    EvidenceChannelStarted = 0,
    SecureListenersReady = 1,
    TlsClientAuthenticated = 2,
    UdpEndpointBound = 3,
    Phase4FaultCampaignEnabled = 4,
    Phase4SnapshotDropStarted = 5,
    Phase4SnapshotDropWindowCompleted = 6,
    Phase4TlsFallbackObserved = 7,
    Phase4CorrectionForced = 8,
    Phase4TlsNoSwitchbackObserved = 9,
    Phase4FaultCampaignExpired = 10,
    SecureServerStopping = 11,
    TlsPolicyAccepted = 12,
    AcceptedSecurePrefaceResponseWritten = 13,
    AuthoritativeUdpMovementAccepted = 14,
    AuthoritativeUdpSnapshotQueued = 15
}

internal static class ControlledHostPrivacyEvidence
{
    internal const string PathEnvironmentVariable =
        "GODSWAR_CONTROLLED_HOST_EVIDENCE_PATH";

    private static ControlledHostPrivacyEvidenceSession? _active;

    internal static IDisposable? TryInstallFromEnvironment()
    {
        var configuredPath =
            Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        if (string.IsNullOrEmpty(configuredPath))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(configuredPath) ||
            !Path.IsPathFullyQualified(configuredPath))
        {
            throw new InvalidOperationException(
                "The controlled-host evidence path must be absolute.");
        }

        var path = Path.GetFullPath(configuredPath);
        if (!path.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The controlled-host evidence path must name a log file.");
        }
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) ||
            !Directory.Exists(directory))
        {
            throw new InvalidOperationException(
                "The controlled-host evidence directory must already exist.");
        }

        var session = new ControlledHostPrivacyEvidenceSession(
            path,
            Console.Out,
            Console.Error);
        if (Interlocked.CompareExchange(
                ref _active,
                session,
                null) is not null)
        {
            session.Abort();
            throw new InvalidOperationException(
                "Controlled-host evidence is already active.");
        }

        try
        {
            session.Install();
            Record(ControlledHostEvidenceEvent.EvidenceChannelStarted);
            return session;
        }
        catch
        {
            Interlocked.CompareExchange(ref _active, null, session);
            session.Abort();
            throw;
        }
    }

    internal static void Record(ControlledHostEvidenceEvent evidence)
    {
        var active = Volatile.Read(ref _active);
        if (active is not null)
        {
            active.Record(evidence);
            return;
        }

        Console.WriteLine(GetLine(evidence));
    }

    internal static void RecordIfActive(
        ControlledHostEvidenceEvent evidence)
    {
        Volatile.Read(ref _active)?.Record(evidence);
    }

    internal static string GetLine(
        ControlledHostEvidenceEvent evidence) =>
        evidence switch
        {
            ControlledHostEvidenceEvent.EvidenceChannelStarted =>
                "[controlled-host] privacy-safe evidence channel started",
            ControlledHostEvidenceEvent.SecureListenersReady =>
                "[controlled-host] secure listeners ready",
            ControlledHostEvidenceEvent.TlsClientAuthenticated =>
                "[controlled-host] TLS client authenticated",
            ControlledHostEvidenceEvent.UdpEndpointBound =>
                "[controlled-host] UDP endpoint authenticated and bound",
            ControlledHostEvidenceEvent.Phase4FaultCampaignEnabled =>
                "[secure-acceptance] phase4 fault campaign enabled",
            ControlledHostEvidenceEvent.Phase4SnapshotDropStarted =>
                "[secure-acceptance] snapshot ACK drop started window_ms=1500 max_recorded_drops=32",
            ControlledHostEvidenceEvent
                    .Phase4SnapshotDropWindowCompleted =>
                "[secure-acceptance] snapshot ACK drop window completed",
            ControlledHostEvidenceEvent.Phase4TlsFallbackObserved =>
                "[secure-acceptance] one-way TLS fallback observed",
            ControlledHostEvidenceEvent.Phase4CorrectionForced =>
                "[secure-acceptance] authoritative correction forced reason=not_ready",
            ControlledHostEvidenceEvent
                    .Phase4TlsNoSwitchbackObserved =>
                "[secure-acceptance] post-fallback TLS movement observed no_switchback=true",
            ControlledHostEvidenceEvent.Phase4FaultCampaignExpired =>
                "[secure-acceptance] phase4 fault campaign expired",
            ControlledHostEvidenceEvent.SecureServerStopping =>
                "[controlled-host] secure server stopping",
            ControlledHostEvidenceEvent.TlsPolicyAccepted =>
                "[controlled-host] TLS policy accepted",
            ControlledHostEvidenceEvent
                    .AcceptedSecurePrefaceResponseWritten =>
                "[controlled-host] accepted secure preface response written",
            ControlledHostEvidenceEvent
                    .AuthoritativeUdpMovementAccepted =>
                "[secure-acceptance] authoritative UDP movement accepted",
            ControlledHostEvidenceEvent
                    .AuthoritativeUdpSnapshotQueued =>
                "[secure-acceptance] authoritative UDP snapshot queued",
            _ => throw new ArgumentOutOfRangeException(
                nameof(evidence),
                evidence,
                "Unknown controlled-host evidence event.")
        };

    private sealed class ControlledHostPrivacyEvidenceSession :
        IDisposable
    {
        private const int MaximumEvidenceEvents = 16;
        private const int MaximumEvidenceBytes = 1_536;

        private readonly object _gate = new();
        private readonly TextWriter _originalOutput;
        private readonly TextWriter _originalError;
        private readonly FileStream _stream;
        private readonly StreamWriter _writer;
        private ulong _recorded;
        private int _eventCount;
        private int _bytesWritten;
        private bool _installed;
        private bool _disposed;

        internal ControlledHostPrivacyEvidenceSession(
            string path,
            TextWriter originalOutput,
            TextWriter originalError)
        {
            _originalOutput = originalOutput;
            _originalError = originalError;
            _stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4_096,
                FileOptions.WriteThrough);
            _writer = new StreamWriter(
                _stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1_024,
                leaveOpen: false)
            {
                AutoFlush = true
            };
        }

        internal void Install()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_installed)
                {
                    throw new InvalidOperationException(
                        "Controlled-host evidence is already installed.");
                }

                // No ordinary game log is forwarded or buffered. Only calls
                // to Record(enum) can reach the evidence sink.
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);
                _installed = true;
            }
        }

        internal void Record(ControlledHostEvidenceEvent evidence)
        {
            var ordinal = (int)evidence;
            if ((uint)ordinal >= MaximumEvidenceEvents)
            {
                throw new ArgumentOutOfRangeException(nameof(evidence));
            }

            var mask = 1UL << ordinal;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if ((_recorded & mask) != 0)
                {
                    return;
                }

                var line = GetLine(evidence);
                var byteCount =
                    Encoding.UTF8.GetByteCount(line) +
                    Encoding.UTF8.GetByteCount(Environment.NewLine);
                if (_eventCount >= MaximumEvidenceEvents ||
                    _bytesWritten + byteCount > MaximumEvidenceBytes)
                {
                    throw new InvalidOperationException(
                        "Controlled-host evidence exceeded its fixed bound.");
                }

                _writer.WriteLine(line);
                _recorded |= mask;
                _eventCount++;
                _bytesWritten += byteCount;
                WriteOperatorLine(line);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_installed)
                {
                    RecordWhileLocked(
                        ControlledHostEvidenceEvent.SecureServerStopping);
                    Console.SetOut(_originalOutput);
                    Console.SetError(_originalError);
                    _installed = false;
                }
                _writer.Dispose();
                _disposed = true;
            }
            Interlocked.CompareExchange(
                ref _active,
                null,
                this);
        }

        internal void Abort()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                if (_installed)
                {
                    Console.SetOut(_originalOutput);
                    Console.SetError(_originalError);
                    _installed = false;
                }
                _writer.Dispose();
                _disposed = true;
            }
        }

        private void RecordWhileLocked(
            ControlledHostEvidenceEvent evidence)
        {
            var ordinal = (int)evidence;
            var mask = 1UL << ordinal;
            if ((_recorded & mask) != 0)
            {
                return;
            }

            var line = GetLine(evidence);
            var byteCount =
                Encoding.UTF8.GetByteCount(line) +
                Encoding.UTF8.GetByteCount(Environment.NewLine);
            if (_eventCount >= MaximumEvidenceEvents ||
                _bytesWritten + byteCount > MaximumEvidenceBytes)
            {
                return;
            }
            _writer.WriteLine(line);
            _recorded |= mask;
            _eventCount++;
            _bytesWritten += byteCount;
            WriteOperatorLine(line);
        }

        private void WriteOperatorLine(string line)
        {
            try
            {
                _originalOutput.WriteLine(line);
                _originalOutput.Flush();
            }
            catch
            {
                // The evidence file is authoritative for this local gate.
                // A detached or closed operator console must not terminate
                // the server after the fixed record has been committed.
            }
        }
    }
}
