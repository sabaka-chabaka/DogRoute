using System.Net;
using System.Net.Sockets;

namespace DogRoute.Server.Tests.Support;

internal sealed class TestServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _run;

    public int Port { get; }

    public TestServer(DogHandler handler)
    {
        Port = GetFreePort();
        var server = new DogServer(new IPEndPoint(IPAddress.Loopback, Port), handler);
        _run = server.RunAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _run;
        _cts.Dispose();
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
