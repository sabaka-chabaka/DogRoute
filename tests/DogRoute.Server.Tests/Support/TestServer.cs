using System.Net;
using System.Net.Sockets;

namespace DogRoute.Server.Tests.Support;

internal sealed class TestServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    public Task Completion { get; }

    public TestServer(DogHandler handler, DogServerOptions? options = null)
    {
        Port = GetFreePort();
        var server = new DogServer(new IPEndPoint(IPAddress.Loopback, Port), handler, options);
        Completion = server.RunAsync(_cts.Token);
    }

    public void Stop() => _cts.Cancel();

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await Completion;
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
