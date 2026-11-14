namespace DogRoute.Routing;

internal readonly struct RouteParam(byte[] name, int segment, bool catchAll)
{
    public byte[] Name { get; } = name;
    public int Segment { get; } = segment;
    public bool CatchAll { get; } = catchAll;
}

internal sealed class RouteEndpoint(string pattern, DogRouteHandler handler, RouteParam[] parameters)
{
    public string Pattern { get; } = pattern;
    public DogRouteHandler Handler { get; } = handler;
    public RouteParam[] Params { get; } = parameters;
}
