using System.Buffers;
using System.Text;

namespace DogRoute.Server;

public static class ContentTypes
{
    public static readonly byte[] Text  = "text/plain; charset=utf-8"u8.ToArray();
    public static readonly byte[] Html  = "text/html; charset=utf-8"u8.ToArray();
    public static readonly byte[] Json  = "application/json"u8.ToArray();
    public static readonly byte[] Octet = "application/octet-stream"u8.ToArray();
}

public sealed class DogResponse : IBufferWriter<byte>, IDisposable
{
    private readonly ByteBuffer _body = new(1024);

    public int StatusCode { get; set; } = 200;
    public byte[] ContentType { get; set; } = ContentTypes.Text;

    internal ReadOnlySpan<byte> BodySpan => _body.WrittenSpan;
    internal int BodyLength => _body.Count;

    internal void Reset()
    {
        StatusCode = 200;
        ContentType = ContentTypes.Text;
        _body.Clear();
    }

    public void Write(ReadOnlySpan<byte> data) => _body.Write(data);

    public void Write(ReadOnlySpan<char> text)
    {
        Span<byte> span = _body.GetSpan(Encoding.UTF8.GetMaxByteCount(text.Length));
        _body.Advance(Encoding.UTF8.GetBytes(text, span));
    }

    public void Advance(int count) => _body.Advance(count);
    public Memory<byte> GetMemory(int sizeHint = 0) => _body.GetMemory(sizeHint);
    public Span<byte> GetSpan(int sizeHint = 0) => _body.GetSpan(sizeHint);

    public void Dispose() => _body.Dispose();
}