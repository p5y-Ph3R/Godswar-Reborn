using System.Net;
using System.Security.Cryptography.X509Certificates;
using Godswar.Server.Networking.Backhaul;

namespace Godswar.Server.Networking.SemanticGateway;

internal static partial class SemanticGatewayGameConnection
{
    private const int MaximumAccountReleaseAttempts = 5;
    private static readonly TimeSpan InitialAccountReleaseDelay =
        TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan MaximumAccountReleaseDelay =
        TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// A replacement relay closes its old mTLS socket before releasing the
    /// process-local coordinator. The worker can observe that close a few
    /// milliseconds later. Retry only that transient account-ownership
    /// status, with the same reserved claim and a finite exponential delay.
    /// Every other worker rejection remains immediately authoritative.
    /// </summary>
    private static async Task<GatewayBackhaulConnection>
        ConnectToWorkerAsync(
            IPEndPoint workerEndpoint,
            string workerTlsHost,
            X509Certificate2 gatewayCertificate,
            BackhaulCertificatePins allowedWorkerCertificates,
            BackhaulHandshakeGate handshakeGate,
            GatewayWorldAdmission admission,
            BackhaulRuntimeLimits limits,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
    {
        var delay = InitialAccountReleaseDelay;
        var retryStartedAt = timeProvider.GetTimestamp();
        var admissionLifetime =
            admission.ExpiresAtUtc - admission.IssuedAtUtc;
        for (var attempt = 1;
             attempt <= MaximumAccountReleaseAttempts;
             attempt++)
        {
            try
            {
                return await GatewayBackhaulClient.ConnectAsync(
                    workerEndpoint,
                    workerTlsHost,
                    gatewayCertificate,
                    allowedWorkerCertificates,
                    handshakeGate,
                    admission,
                    limits,
                    timeProvider,
                    cancellationToken);
            }
            catch (BackhaulAdmissionRejectedException error)
                when (error.Status ==
                        BackhaulAdmissionStatus.AccountAlreadyActive &&
                    attempt < MaximumAccountReleaseAttempts &&
                    RetryFitsAdmissionLifetime(
                        timeProvider,
                        retryStartedAt,
                        admissionLifetime,
                        delay))
            {
                await Task.Delay(
                    delay,
                    timeProvider,
                    cancellationToken);
                delay = TimeSpan.FromTicks(
                    Math.Min(
                        delay.Ticks * 2,
                        MaximumAccountReleaseDelay.Ticks));
            }
        }

        throw new InvalidOperationException(
            "The bounded worker connection retry did not terminate.");
    }

    private static bool RetryFitsAdmissionLifetime(
        TimeProvider timeProvider,
        long startedAtTimestamp,
        TimeSpan admissionLifetime,
        TimeSpan delay)
    {
        var elapsed =
            timeProvider.GetElapsedTime(startedAtTimestamp);
        return elapsed >= TimeSpan.Zero &&
            elapsed + delay < admissionLifetime;
    }
}
