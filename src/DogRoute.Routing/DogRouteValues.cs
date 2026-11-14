using System.Buffers.Text;
using System.Text;

namespace DogRoute.Routing;

public sealed class DogRouteValues
{
    internal readonly int[] SegStart = new int[RouteTree.MaxSegments];
    internal readonly int[] SegLength = new int[RouteTree.MaxSegments];
    internal int SegCount;
    internal int BodyEnd;
    internal ReadOnlyMemory<byte> Path;
    internal RouteEndpoint? Endpoint;

    internal DogRouteValues() { }

    public int Count => Endpoint?.Params.Length ?? 0;

    public ReadOnlySpan<byte> this[ReadOnlySpan<byte> name] => TryGet(name, out ReadOnlySpan<byte> value) ? value : default;

    public bool TryGet(ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        value = default;

        RouteEndpoint? endpoint = Endpoint;
        if (endpoint is null) return false;

        RouteParam[] parameters = endpoint.Params;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (Ascii.EqualsIgnoreCase(parameters[i].Name.AsSpan(), name))
            {
                value = ValueAt(parameters[i]);
                return true;
            }
        }

        return false;
    }

    public bool TryGetInt32(ReadOnlySpan<byte> name, out int value)
    {
        value = 0;
        return TryGet(name, out ReadOnlySpan<byte> raw)
            && Utf8Parser.TryParse(raw, out value, out int consumed)
            && consumed == raw.Length;
    }

    public bool TryGetInt64(ReadOnlySpan<byte> name, out long value)
    {
        value = 0;
        return TryGet(name, out ReadOnlySpan<byte> raw)
            && Utf8Parser.TryParse(raw, out value, out int consumed)
            && consumed == raw.Length;
    }

    public bool TryGetGuid(ReadOnlySpan<byte> name, out Guid value)
    {
        value = Guid.Empty;
        return TryGet(name, out ReadOnlySpan<byte> raw)
            && Utf8Parser.TryParse(raw, out value, out int consumed)
            && consumed == raw.Length;
    }

    public string? GetString(ReadOnlySpan<byte> name) =>
        TryGet(name, out ReadOnlySpan<byte> raw) ? Encoding.UTF8.GetString(raw) : null;

    public void GetAt(int index, out ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        RouteParam[]? parameters = Endpoint?.Params;
        if (parameters is null || (uint)index >= (uint)parameters.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        name = parameters[index].Name;
        value = ValueAt(parameters[index]);
    }

    internal void Clear()
    {
        Endpoint = null;
        Path = default;
        SegCount = 0;
        BodyEnd = 0;
    }

    private ReadOnlySpan<byte> ValueAt(in RouteParam parameter)
    {
        ReadOnlySpan<byte> path = Path.Span;

        if (parameter.CatchAll)
        {
            if (parameter.Segment >= SegCount) return default;

            int start = SegStart[parameter.Segment];
            return path[start..BodyEnd];
        }

        return path.Slice(SegStart[parameter.Segment], SegLength[parameter.Segment]);
    }
}
