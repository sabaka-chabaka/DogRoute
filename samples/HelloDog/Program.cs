using System.Net;
using DogRoute.Server;

int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 8080;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var server = new DogServer(new IPEndPoint(IPAddress.IPv6Any, port), static (req, res) =>
{
    ReadOnlySpan<byte> path = req.Path;

    if (path.SequenceEqual("/echo"u8))
    {
        res.ContentType = ContentTypes.Octet;
        res.Write(req.Body);
    }
    else if (path.SequenceEqual("/ping"u8))
    {
        res.Write("pong"u8);
    }
    else
    {
        res.Write("Hello, World!"u8);
    }

    return ValueTask.CompletedTask;
});

Console.WriteLine($"DogRoute listening on http://localhost:{port}  (Ctrl+C to stop)");
await server.RunAsync(cts.Token);
