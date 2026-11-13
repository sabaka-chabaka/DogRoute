using System.Net;
using DogRoute.Server;

int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 8080;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("Stopping (waiting for active requests)...");
    cts.Cancel();
};

var server = new DogServer(new IPEndPoint(IPAddress.IPv6Any, port), static (req, res) =>
{
    ReadOnlySpan<byte> path = req.DecodedPath;

    if (path.SequenceEqual("/echo"u8))
    {
        res.ContentType = ContentTypes.Octet;
        res.Write(req.Body);
    }
    else if (path.SequenceEqual("/ping"u8))
    {
        res.Write("pong"u8);
    }
    else if (path.SequenceEqual("/redirect"u8))
    {
        res.Redirect("/"u8);
    }
    else if (path.SequenceEqual("/headers"u8))
    {
        res.Headers.Add("X-Dog"u8, "woof"u8);
        res.Headers.Add("Set-Cookie", "a=1; Path=/");
        res.Headers.Add("Set-Cookie", "b=2; Path=/");
        res.Write("headers"u8);
    }
    else if (path.StartsWith("/path/"u8))
    {
        res.Write(path);
    }
    else if (path.SequenceEqual("/slow"u8))
    {
        return SlowAsync(res);
    }
    else
    {
        res.Write("Hello, World!"u8);
    }

    return ValueTask.CompletedTask;
});

Console.WriteLine($"DogRoute listening on http://localhost:{port}  (Ctrl+C to stop)");
await server.RunAsync(cts.Token);
Console.WriteLine("Stopped.");

static async ValueTask SlowAsync(DogResponse res)
{
    await Task.Delay(3000);
    res.Write("slow done"u8);
}
