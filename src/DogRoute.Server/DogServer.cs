using System.Net;
using System.Net.Sockets;

namespace DogRoute.Server;

public delegate ValueTask DogHandler(DogRequest request, DogResponse response);

public sealed class DogServer(IPEndPoint endPoint, DogHandler handler, DogServerOptions? options = null)
{
    private readonly DogServerOptions _options = Validated(options);

    public async Task RunAsync(CancellationToken stopToken = default)
    {
        using var listener = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (endPoint.AddressFamily == AddressFamily.InterNetworkV6)
            listener.DualMode = true;

        listener.Bind(endPoint);
        listener.Listen(1024);

        var manager = new ConnectionManager();
        using var heartbeatCts = new CancellationTokenSource();
        Task heartbeat = manager.RunHeartbeatAsync(_options.HeartbeatInterval, heartbeatCts.Token);

        try
        {
            while (true)
            {
                Socket client;
                try
                {
                    client = await listener.AcceptAsync(stopToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    await Task.Delay(10);
                    continue;
                }

                try
                {
                    client.NoDelay = true;
                }
                catch (SocketException)
                {
                    client.Dispose();
                    continue;
                }

                var connection = new DogConnection(client, handler, _options, manager);
                manager.Add(connection);
                _ = connection.RunAsync();
            }
        }
        finally
        {
            listener.Close();
            await manager.DrainAsync(_options.ShutdownTimeout);
            await heartbeatCts.CancelAsync();
            await heartbeat;
        }
    }

    private static DogServerOptions Validated(DogServerOptions? options)
    {
        options ??= new DogServerOptions();
        options.Validate();
        return options;
    }
}
