using System.Net;
using System.Net.Sockets;

namespace DogRoute.Routing.Tests.Support;

internal sealed class TestHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _run;

    public int Port { get; }

    public TestHost(Action<DogApp> configure)
    {
        var app = new DogApp();
        configure(app);

        Port = GetFreePort();
        _run = app.RunAsync(new IPEndPoint(IPAddress.Loopback, Port), null, _cts.Token);
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
