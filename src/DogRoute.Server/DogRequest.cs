using System.Buffers;
using System.Text;

namespace DogRoute.Server;

public enum DogMethod : byte { Other, Get, Post, Put, Delete, Head, Patch, Options }

public sealed class DogRequest : IDisposable
{
    internal struct HeaderEntry
    {
        public int NameStart, NameLength, ValueStart, ValueLength;
    }

    internal readonly HeaderEntry[] _headers = new HeaderEntry[HttpParser.MaxHeaders];
    internal int _headerCount;
    internal ReadOnlyMemory<byte> _head;
    internal ReadOnlyMemory<byte> _body;
    internal int _methodLength, _targetStart, _targetLength, _pathLength;

    private byte[]? _headScratch;
    private byte[]? _bodyScratch;
    private byte[]? _pathScratch;
    private int _decodedPathLength;
    private bool _pathDecoded;

    public DogMethod Method { get; internal set; }
    public bool KeepAlive { get; internal set; }
    public bool IsHttp10 { get; internal set; }
    public bool ExpectContinue { get; internal set; }

    public ReadOnlySpan<byte> RawMethod => _head.Span[.._methodLength];
    public ReadOnlySpan<byte> Target => _head.Span.Slice(_targetStart, _targetLength);

    public ReadOnlySpan<byte> Path => _head.Span.Slice(_targetStart, _pathLength);

    public ReadOnlySpan<byte> DecodedPath =>
        _pathDecoded ? _pathScratch.AsSpan(0, _decodedPathLength) : Path;

    public ReadOnlySpan<byte> Query => _pathLength >= _targetLength
        ? default
        : _head.Span.Slice(_targetStart + _pathLength + 1, _targetLength - _pathLength - 1);

    public ReadOnlySpan<byte> Body => _body.Span;
    public ReadOnlyMemory<byte> BodyMemory => _body;
    public int HeaderCount => _headerCount;

    public void GetHeader(int index, out ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        if ((uint)index >= (uint)_headerCount) throw new ArgumentOutOfRangeException(nameof(index));

        ref readonly HeaderEntry h = ref _headers[index];
        ReadOnlySpan<byte> head = _head.Span;
        name = head.Slice(h.NameStart, h.NameLength);
        value = head.Slice(h.ValueStart, h.ValueLength);
    }

    public bool TryGetHeader(ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> head = _head.Span;
        for (int i = 0; i < _headerCount; i++)
        {
            ref readonly HeaderEntry h = ref _headers[i];
            if (Ascii.EqualsIgnoreCase(head.Slice(h.NameStart, h.NameLength), name))
            {
                value = head.Slice(h.ValueStart, h.ValueLength);
                return true;
            }
        }
        value = default;
        return false;
    }

    internal void Reset()
    {
        _headerCount = 0;
        _head = default;
        _body = default;
        _methodLength = _targetStart = _targetLength = _pathLength = 0;
        _decodedPathLength = 0;
        _pathDecoded = false;
        Method = DogMethod.Other;
        KeepAlive = true;
        IsHttp10 = false;
        ExpectContinue = false;
    }

    internal bool NormalizePath()
    {
        ReadOnlySpan<byte> raw = Path;

        if (!UriPath.NeedsNormalization(raw))
        {
            _pathDecoded = false;
            return true;
        }

        byte[] scratch = Rent(ref _pathScratch, raw.Length);
        if (!UriPath.TryNormalize(raw, scratch, out int length)) return false;

        _decodedPathLength = length;
        _pathDecoded = true;
        return true;
    }

    internal ReadOnlyMemory<byte> CopyHead(in ReadOnlySequence<byte> src) => Copy(ref _headScratch, src);
    internal ReadOnlyMemory<byte> CopyBody(in ReadOnlySequence<byte> src) => Copy(ref _bodyScratch, src);

    internal Memory<byte> AllocBody(int length) => Rent(ref _bodyScratch, length).AsMemory(0, length);

    private static ReadOnlyMemory<byte> Copy(ref byte[]? scratch, in ReadOnlySequence<byte> src)
    {
        int length = checked((int)src.Length);
        byte[] buffer = Rent(ref scratch, length);

        src.CopyTo(buffer.AsSpan());
        return buffer.AsMemory(0, length);
    }

    private static byte[] Rent(ref byte[]? scratch, int length)
    {
        if (scratch is null || scratch.Length < length)
        {
            if (scratch is not null) ArrayPool<byte>.Shared.Return(scratch);
            scratch = ArrayPool<byte>.Shared.Rent(Math.Max(length, 4096));
        }

        return scratch;
    }

    public void Dispose()
    {
        if (_headScratch is not null) ArrayPool<byte>.Shared.Return(_headScratch);
        if (_bodyScratch is not null) ArrayPool<byte>.Shared.Return(_bodyScratch);
        if (_pathScratch is not null) ArrayPool<byte>.Shared.Return(_pathScratch);
        _headScratch = _bodyScratch = _pathScratch = null;
    }
}
