using System.Net;
using System.Net.Sockets;

namespace Godswar.Server.Networking;

internal sealed class TcpEndpointServer
{
    private readonly string _name;
    private readonly string _host;
    private readonly int _port;
    private readonly Func<ClientSession, IClientHandler> _handlerFactory;

    public TcpEndpointServer(string name, string host, int port, Func<ClientSession, IClientHandler> handlerFactory)
    {
        _name = name;
        _host = host;
        _port = port;
        _handlerFactory = handlerFactory;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var address = ResolveAddress(_host);
        var listener = new TcpListener(address, _port);
        listener.Start();

        Console.WriteLine($"[{_name}] listening on {address}:{_port}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var session = new ClientSession(new RawTcpLegacyTransport(client));
        Console.WriteLine($"[{_name}] connected {session.RemoteEndPoint}");

        try
        {
            await _handlerFactory(session).RunAsync(cancellationToken);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[{_name}] disconnected {session.RemoteEndPoint}: {ex.Message}");
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"[{_name}] socket closed {session.RemoteEndPoint}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{_name}] error {session.RemoteEndPoint}: {ex}");
        }
        finally
        {
            Console.WriteLine($"[{_name}] closed {session.RemoteEndPoint}");
        }
    }

    private static IPAddress ResolveAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host is "*" or "0.0.0.0")
        {
            return IPAddress.Any;
        }

        return IPAddress.TryParse(host, out var address)
            ? address
            : Dns.GetHostAddresses(host).First();
    }
}
