using System.Net;
using DogRoute.Server;

namespace DogRoute.Routing;

public sealed class DogApp
{
    private readonly RouteTree _tree = new();
    private readonly ContextPool _pool = new();
    private DogHandler? _handler;

    public DogApp Map(DogMethod method, string pattern, DogRouteHandler handler)
    {
        if (_handler is not null)
            throw new InvalidOperationException("Routes cannot be added after the app has started.");

        _tree.Add(method, pattern, handler);
        return this;
    }

    public DogRouteGroup MapGroup(string prefix) => new(this, DogRouteGroup.NormalizePrefix(prefix));

    public DogHandler Handler => _handler ??= new DogHandler(Dispatch);

    public Task RunAsync(IPEndPoint endPoint, DogServerOptions? options = null, CancellationToken stopToken = default) =>
        new DogServer(endPoint, Handler, options).RunAsync(stopToken);

    private ValueTask Dispatch(DogRequest request, DogResponse response)
    {
        DogContext context = _pool.Rent();
        context.Init(request, response);

        RouteEndpoint? endpoint = _tree.Match(request.Method, context.Route, out int allowed);
        if (endpoint is null)
        {
            WriteNotMatched(response, allowed);
            _pool.Return(context);
            return ValueTask.CompletedTask;
        }

        ValueTask task;
        try
        {
            task = endpoint.Handler(context);
        }
        catch
        {
            _pool.Return(context);
            throw;
        }

        if (task.IsCompletedSuccessfully)
        {
            task.GetAwaiter().GetResult();
            _pool.Return(context);
            return ValueTask.CompletedTask;
        }

        return AwaitAndRecycle(task, context);
    }

    private async ValueTask AwaitAndRecycle(ValueTask task, DogContext context)
    {
        try
        {
            await task;
        }
        finally
        {
            _pool.Return(context);
        }
    }

    private static void WriteNotMatched(DogResponse response, int allowed)
    {
        if (allowed == 0)
        {
            response.StatusCode = 404;
            response.Write("Not Found"u8);
            return;
        }

        response.StatusCode = 405;

        Span<byte> buffer = stackalloc byte[64];
        int length = FormatAllow(allowed, buffer);
        response.Headers.Add("Allow"u8, buffer[..length]);
        response.Write("Method Not Allowed"u8);
    }

    private static int FormatAllow(int allowed, Span<byte> destination)
    {
        int length = 0;

        if (Has(allowed, DogMethod.Get))
        {
            Append(destination, ref length, "GET"u8);
            Append(destination, ref length, "HEAD"u8);
        }
        else if (Has(allowed, DogMethod.Head))
        {
            Append(destination, ref length, "HEAD"u8);
        }

        if (Has(allowed, DogMethod.Post)) Append(destination, ref length, "POST"u8);
        if (Has(allowed, DogMethod.Put)) Append(destination, ref length, "PUT"u8);
        if (Has(allowed, DogMethod.Patch)) Append(destination, ref length, "PATCH"u8);
        if (Has(allowed, DogMethod.Delete)) Append(destination, ref length, "DELETE"u8);
        if (Has(allowed, DogMethod.Options)) Append(destination, ref length, "OPTIONS"u8);

        return length;
    }

    private static bool Has(int allowed, DogMethod method) => (allowed & (1 << (int)method)) != 0;

    private static void Append(Span<byte> destination, ref int length, ReadOnlySpan<byte> name)
    {
        if (length > 0)
        {
            ", "u8.CopyTo(destination[length..]);
            length += 2;
        }

        name.CopyTo(destination[length..]);
        length += name.Length;
    }
}
