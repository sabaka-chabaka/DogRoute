using System.Text;
using DogRoute.Server;

namespace DogRoute.Routing.Tests;

internal static class RouteKit
{
    public static readonly DogRouteHandler Noop = static _ => ValueTask.CompletedTask;

    public static RouteTree Tree(params (DogMethod Method, string Pattern)[] routes)
    {
        var tree = new RouteTree();
        foreach ((DogMethod method, string pattern) in routes)
            tree.Add(method, pattern, Noop);
        return tree;
    }

    public static RouteTree GetTree(params string[] patterns)
    {
        var tree = new RouteTree();
        foreach (string pattern in patterns)
            tree.Add(DogMethod.Get, pattern, Noop);
        return tree;
    }

    public static RouteEndpoint? Match(
        RouteTree tree, DogMethod method, string path, out DogRouteValues values, out int allowed)
    {
        values = new DogRouteValues { Path = Encoding.UTF8.GetBytes(path) };
        return tree.Match(method, values, out allowed);
    }
}
