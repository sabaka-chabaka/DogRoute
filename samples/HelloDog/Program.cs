using System.Net;
using DogRoute.Routing;
using DogRoute.Server;

int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 8080;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("Stopping (waiting for active requests)...");
    cts.Cancel();
};

var app = new DogApp();

app.Map(DogMethod.Get, "/", static ctx =>
{
    ctx.Response.Write("Hello, World!"u8);
    return ValueTask.CompletedTask;
});

app.Map(DogMethod.Get, "/ping", static ctx =>
{
    ctx.Response.Write("pong"u8);
    return ValueTask.CompletedTask;
});

app.Map(DogMethod.Get, "/redirect", static ctx =>
{
    ctx.Response.Redirect("/"u8);
    return ValueTask.CompletedTask;
});

app.Map(DogMethod.Get, "/files/{*path}", static ctx =>
{
    ctx.Response.Write("file: "u8);
    ctx.Response.Write(ctx.Route["path"u8]);
    return ValueTask.CompletedTask;
});

app.Map(DogMethod.Get, "/slow", static async ctx =>
{
    await Task.Delay(3000);
    ctx.Response.Write("slow done"u8);
});

RouteGroupDemo(app);

Console.WriteLine($"DogRoute listening on http://localhost:{port}  (Ctrl+C to stop)");
await app.RunAsync(new IPEndPoint(IPAddress.IPv6Any, port), stopToken: cts.Token);
Console.WriteLine("Stopped.");

static void RouteGroupDemo(DogApp app)
{
    DogRouteGroup api = app.MapGroup("/api");

    api.Map(DogMethod.Get, "/dogs/{id:int}", static ctx =>
    {
        ctx.Response.Write("dog #"u8);
        ctx.Response.Write(ctx.Route["id"u8]);
        return ValueTask.CompletedTask;
    });

    api.Map(DogMethod.Get, "/dogs/{name}", static ctx =>
    {
        ctx.Response.Write("dog named "u8);
        ctx.Response.Write(ctx.Route["name"u8]);
        return ValueTask.CompletedTask;
    });

    api.Map(DogMethod.Post, "/echo", static ctx =>
    {
        ctx.Response.ContentType = ContentTypes.Octet;
        ctx.Response.Write(ctx.Request.Body);
        return ValueTask.CompletedTask;
    });
}
