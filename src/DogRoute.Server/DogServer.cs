using System.Net;
using System.Net.Sockets;

namespace DogRoute.Server;

public delegate ValueTask DogHandler(DogRequest request, DogResponse response);

public sealed class DogServer(IPEndPoint endPoint, DogHandler handler)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        using var listener = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (endPoint.AddressFamily == AddressFamily.InterNetworkV6)
            listener.DualMode = true;

        listener.Bind(endPoint);
        listener.Listen(1024);

        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            client.NoDelay = true;
            _ = new DogConnection(client, handler).RunAsync(ct);
        }
    }
}