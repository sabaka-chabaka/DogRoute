using DogRoute.Server;

namespace DogRoute.Routing;

public sealed class DogRouteGroup
{
    private readonly DogApp _app;
    private readonly string _prefix;

    internal DogRouteGroup(DogApp app, string prefix)
    {
        _app = app;
        _prefix = prefix;
    }

    public DogRouteGroup Map(DogMethod method, string pattern, DogRouteHandler handler)
    {
        _app.Map(method, Combine(_prefix, pattern), handler);
        return this;
    }

    public DogRouteGroup MapGroup(string prefix) => new(_app, _prefix + NormalizePrefix(prefix));

    internal static string NormalizePrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        if (prefix.Length == 0 || prefix[0] != '/')
            throw new ArgumentException($"Group prefix must start with '/': \"{prefix}\".", nameof(prefix));

        if (prefix.Length > 1 && prefix[^1] == '/') prefix = prefix[..^1];

        return prefix == "/" ? "" : prefix;
    }

    internal static string Combine(string prefix, string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.Length == 0 || pattern[0] != '/')
            throw new ArgumentException($"Route pattern must start with '/': \"{pattern}\".", nameof(pattern));

        if (prefix.Length == 0) return pattern;

        return pattern == "/" ? prefix : prefix + pattern;
    }
}
