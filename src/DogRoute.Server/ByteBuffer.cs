using System.Buffers;

namespace DogRoute.Server;

internal sealed class ByteBuffer(int initialSize) : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(initialSize);
    private int _count;

    public int Count => _count;
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _count);
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _count);

    public void Clear() => _count = 0;

    public void Advance(int count)
    {
        if (count < 0 || count > _buffer.Length - _count)
            throw new ArgumentOutOfRangeException(nameof(count));
        _count += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_count);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_count);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        data.CopyTo(GetSpan(data.Length));
        _count += data.Length;
    }

    private void Ensure(int sizeHint)
    {
        if (sizeHint < 1) sizeHint = 1;
        if (_buffer.Length - _count >= sizeHint) return;

        byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _count + sizeHint));
        _buffer.AsSpan(0, _count).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = bigger;
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }
}