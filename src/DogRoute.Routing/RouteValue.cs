using System.Buffers.Text;
using System.Text;
using DogRoute.Server;

namespace DogRoute.Routing;

internal sealed class ParamEdge(ConstraintKind kind, RouteNode node)
{
    public ConstraintKind Kind { get; } = kind;
    public RouteNode Node { get; } = node;
}

internal sealed class RouteNode
{
    public byte[][] LiteralKeys = [];
    public RouteNode[] LiteralNodes = [];
    public ParamEdge[] Params = [];
    public RouteNode? CatchAll;
    public RouteEndpoint?[]? Table;
    public int MethodMask;
}

internal sealed class RouteTree
{
    public const int MaxSegments = 32;
    public const int MethodCount = 8;

    private readonly RouteNode _root = new();

    public void Add(DogMethod method, string pattern, DogRouteHandler handler)
    {
        if ((int)method <= 0 || (int)method >= MethodCount)
            throw new ArgumentException("A concrete HTTP method is required (DogMethod.Other cannot be routed).", nameof(method));

        ArgumentNullException.ThrowIfNull(handler);

        List<PatternSegment> segments = RoutePattern.Parse(pattern);
        if (segments.Count > MaxSegments)
            throw new ArgumentException($"Route pattern has more than {MaxSegments} segments: \"{pattern}\".", nameof(pattern));

        RouteNode node = _root;
        var parameters = new List<RouteParam>();

        for (int i = 0; i < segments.Count; i++)
        {
            PatternSegment segment = segments[i];

            switch (segment.Kind)
            {
                case SegmentKind.Literal:
                    node = GetOrAddLiteral(node, Encoding.UTF8.GetBytes(segment.Text));
                    break;

                case SegmentKind.Param:
                    node = GetOrAddParam(node, segment.Constraint);
                    parameters.Add(new RouteParam(Encoding.ASCII.GetBytes(segment.Text), i, catchAll: false));
                    break;

                default: // CatchAll
                    node = node.CatchAll ??= new RouteNode();
                    parameters.Add(new RouteParam(Encoding.ASCII.GetBytes(segment.Text), i, catchAll: true));
                    break;
            }
        }

        node.Table ??= new RouteEndpoint?[MethodCount];

        if (node.Table[(int)method] is not null)
            throw new InvalidOperationException($"Route {method} {pattern} is already registered.");

        node.Table[(int)method] = new RouteEndpoint(pattern, handler, parameters.ToArray());
        node.MethodMask |= 1 << (int)method;
    }

    public RouteEndpoint? Match(DogMethod method, DogRouteValues values, out int allowed)
    {
        allowed = 0;
        values.Endpoint = null;

        ReadOnlySpan<byte> path = values.Path.Span;
        if (!Segment(path, values)) return null;

        RouteEndpoint? endpoint = Find(_root, 0, method, path, values, ref allowed);
        values.Endpoint = endpoint;
        return endpoint;
    }

    private static bool Segment(ReadOnlySpan<byte> path, DogRouteValues values)
    {
        values.SegCount = 0;
        values.BodyEnd = 0;

        if (path.IsEmpty || path[0] != (byte)'/') return false;

        if (path.Length == 1)
        {
            values.BodyEnd = 1;
            return true;
        }

        int end = path[^1] == (byte)'/' ? path.Length - 1 : path.Length;
        int start = 1;

        for (int i = 1; i <= end; i++)
        {
            if (i != end && path[i] != (byte)'/') continue;

            if (values.SegCount == MaxSegments) return false;

            values.SegStart[values.SegCount] = start;
            values.SegLength[values.SegCount] = i - start;
            values.SegCount++;
            start = i + 1;
        }

        values.BodyEnd = end;
        return true;
    }

    private static RouteEndpoint? Find(
        RouteNode node, int index, DogMethod method, ReadOnlySpan<byte> path, DogRouteValues values, ref int allowed)
    {
        if (index == values.SegCount)
        {
            RouteEndpoint? terminal = Pick(node, method, ref allowed);
            if (terminal is not null) return terminal;

            return node.CatchAll is { } emptyCatchAll ? Pick(emptyCatchAll, method, ref allowed) : null;
        }

        ReadOnlySpan<byte> segment = path.Slice(values.SegStart[index], values.SegLength[index]);

        byte[][] keys = node.LiteralKeys;
        for (int k = 0; k < keys.Length; k++)
        {
            if (!keys[k].AsSpan().SequenceEqual(segment)) continue;

            RouteEndpoint? found = Find(node.LiteralNodes[k], index + 1, method, path, values, ref allowed);
            if (found is not null) return found;
            break;
        }

        if (segment.Length > 0)
        {
            ParamEdge[] edges = node.Params;
            for (int p = 0; p < edges.Length; p++)
            {
                if (!Satisfies(edges[p].Kind, segment)) continue;

                RouteEndpoint? found = Find(edges[p].Node, index + 1, method, path, values, ref allowed);
                if (found is not null) return found;
            }
        }

        return node.CatchAll is { } catchAll ? Pick(catchAll, method, ref allowed) : null;
    }

    private static RouteEndpoint? Pick(RouteNode node, DogMethod method, ref int allowed)
    {
        RouteEndpoint?[]? table = node.Table;
        if (table is null) return null;

        RouteEndpoint? endpoint = table[(int)method];
        if (endpoint is null && method == DogMethod.Head)
            endpoint = table[(int)DogMethod.Get];

        if (endpoint is not null) return endpoint;

        allowed |= node.MethodMask;
        return null;
    }

    private static bool Satisfies(ConstraintKind kind, ReadOnlySpan<byte> segment)
    {
        int consumed;

        switch (kind)
        {
            case ConstraintKind.Int:
                return Utf8Parser.TryParse(segment, out int _, out consumed) && consumed == segment.Length;
            case ConstraintKind.Long:
                return Utf8Parser.TryParse(segment, out long _, out consumed) && consumed == segment.Length;
            case ConstraintKind.Guid:
                return Utf8Parser.TryParse(segment, out Guid _, out consumed) && consumed == segment.Length;
            default:
                return true;
        }
    }

    private static RouteNode GetOrAddLiteral(RouteNode node, byte[] key)
    {
        for (int i = 0; i < node.LiteralKeys.Length; i++)
        {
            if (node.LiteralKeys[i].AsSpan().SequenceEqual(key.AsSpan()))
                return node.LiteralNodes[i];
        }

        var child = new RouteNode();
        int count = node.LiteralKeys.Length;

        Array.Resize(ref node.LiteralKeys, count + 1);
        Array.Resize(ref node.LiteralNodes, count + 1);
        node.LiteralKeys[count] = key;
        node.LiteralNodes[count] = child;

        return child;
    }

    private static RouteNode GetOrAddParam(RouteNode node, ConstraintKind kind)
    {
        foreach (ParamEdge edge in node.Params)
        {
            if (edge.Kind == kind) return edge.Node;
        }

        var child = new RouteNode();
        var edges = new ParamEdge[node.Params.Length + 1];
        node.Params.CopyTo(edges, 0);
        edges[^1] = new ParamEdge(kind, child);

        Array.Sort(edges, static (a, b) => a.Kind.CompareTo(b.Kind));
        node.Params = edges;

        return child;
    }
}
