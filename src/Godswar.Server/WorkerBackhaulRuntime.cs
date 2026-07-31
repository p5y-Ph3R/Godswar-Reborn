using System.Security.Cryptography.X509Certificates;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Backhaul;
using Godswar.Server.Operations;

namespace Godswar.Server;

/// <summary>
/// Owns the complete lifetime of the worker-side private backhaul listener.
/// A worker process deliberately exposes no public login listener.
/// </summary>
internal sealed class WorkerBackhaulRuntime : IDisposable
{
    private readonly WorkerBackhaulAdmissionRegistry _admissions;
    private readonly X509Certificate2 _certificate;
    private readonly BackhaulHandshakeGate _handshakeGate;
    private int _disposed;

    private WorkerBackhaulRuntime(
        TcpEndpointServer server,
        X509Certificate2 certificate,
        BackhaulHandshakeGate handshakeGate,
        WorkerBackhaulAdmissionRegistry admissions)
    {
        Server = server;
        _certificate = certificate;
        _handshakeGate = handshakeGate;
        _admissions = admissions;
    }

    public TcpEndpointServer Server { get; }

    public void Start(
        ICollection<TcpEndpointServer> endpoints,
        CriticalTaskCollection criticalTasks)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(criticalTasks);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        endpoints.Add(Server);
        criticalTasks.Start(
            CriticalTaskKind.GameListener,
            Server.RunAsync);
    }

    public static void ValidateListenerComposition(
        WorkerBackhaulRuntime? worker,
        int listenerCount)
    {
        if (listenerCount != (worker is null ? 2 : 1))
        {
            throw new InvalidOperationException(
                worker is null
                    ? "The public listener pair is incomplete."
                    : "The private worker listener is incomplete.");
        }
    }

    public static WorkerBackhaulRuntime? TryCreate(
        ServerOptions options,
        IConnectionAdmission connectionAdmission,
        GameClientHandlerFactory gameHandlerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionAdmission);
        ArgumentNullException.ThrowIfNull(gameHandlerFactory);
        if (!options.Backhaul.Enabled)
        {
            return null;
        }

        X509Certificate2? certificate = null;
        BackhaulHandshakeGate? handshakeGate = null;
        WorkerBackhaulAdmissionRegistry? admissions = null;
        try
        {
            certificate = options.Backhaul.LoadCertificate();
            handshakeGate = new BackhaulHandshakeGate(
                options.Backhaul.MaximumConcurrentTlsHandshakes);
            admissions = options.Backhaul.BuildAdmissionRegistry(
                options.Game.WorldInstances);
            var transportFactory =
                new WorkerBackhaulTransportFactory(
                    certificate,
                    options.Backhaul.BuildAllowedGatewayPins(),
                    handshakeGate,
                    admissions,
                    options.Backhaul.RuntimeLimits);
            var server = new TcpEndpointServer(
                NetworkEndpointRole.Game,
                options.Backhaul.BindHost,
                options.Backhaul.Port,
                options.Network,
                connectionAdmission,
                session => gameHandlerFactory.Create(
                    session,
                    legacyAuthenticationAccess: null),
                transportFactory: transportFactory);

            return new WorkerBackhaulRuntime(
                server,
                certificate,
                handshakeGate,
                admissions);
        }
        catch
        {
            admissions?.Dispose();
            handshakeGate?.Dispose();
            certificate?.Dispose();
            throw;
        }
    }

    public void BeginDrain()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _admissions.BeginDrain();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _admissions.BeginDrain();
        _admissions.Dispose();
        _handshakeGate.Dispose();
        _certificate.Dispose();
    }
}
