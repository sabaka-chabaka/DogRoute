using DogRoute.Server;

namespace DogRoute.Routing;

public sealed class DogContext
{
    internal DogContext() { }

    public DogRequest Request { get; private set; } = null!;
    public DogResponse Response { get; private set; } = null!;
    public DogRouteValues Route { get; } = new();

    public string? RoutePattern => Route.Endpoint?.Pattern;

    internal void Init(DogRequest request, DogResponse response)
    {
        Request = request;
        Response = response;
        Route.Path = request.DecodedPathMemory;
    }

    internal void Clear()
    {
        Request = null!;
        Response = null!;
        Route.Clear();
    }
}
