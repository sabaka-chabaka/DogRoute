using System.Text;

namespace DogRoute.Server;

public sealed class DogHeaders
{
    private const int MaxNameLength = 128;

    private readonly ByteBuffer _buffer = new(MaxNameLength);
    private int _count;

    internal DogHeaders() { }

    public int Count => _count;

    internal ReadOnlySpan<byte> Raw => _buffer.WrittenSpan;

    public void Add(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        CheckName(name);
        CheckValue(value);

        _buffer.Write(name);
        _buffer.Write(": "u8);
        _buffer.Write(value);
        _buffer.Write("\r\n"u8);
        _count++;
    }

    public void Add(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        if (name.Length > MaxNameLength)
            throw new ArgumentException("Header name is too long.", nameof(name));

        Span<byte> nameBytes = stackalloc byte[MaxNameLength];
        int n = 0;
        foreach (char c in name)
        {
            if (c > 0x7F) throw new ArgumentException("Header name must be ASCII.", nameof(name));
            nameBytes[n++] = (byte)c;
        }
        CheckName(nameBytes[..n]);

        foreach (char c in value)
        {
            if (c > 0x7E || !IsValueChar(c))
                throw new ArgumentException("Header value contains invalid characters.", nameof(value));
        }

        _buffer.Write(nameBytes[..n]);
        _buffer.Write(": "u8);

        Span<byte> span = _buffer.GetSpan(value.Length);
        _buffer.Advance(Encoding.ASCII.GetBytes(value, span));

        _buffer.Write("\r\n"u8);
        _count++;
    }

    internal void Clear()
    {
        _buffer.Clear();
        _count = 0;
    }

    internal void Release() => _buffer.Dispose();

    private static void CheckName(ReadOnlySpan<byte> name)
    {
        if (name.IsEmpty)
            throw new ArgumentException("Header name is empty.", nameof(name));

        foreach (byte b in name)
        {
            if (!IsTokenChar((char)b))
                throw new ArgumentException("Header name contains invalid characters.", nameof(name));
        }

        if (IsReserved(name))
            throw new ArgumentException("This header is managed by DogRoute and cannot be set manually.", nameof(name));
    }

    private static void CheckValue(ReadOnlySpan<byte> value)
    {
        foreach (byte b in value)
        {
            if (!IsValueChar((char)b))
                throw new ArgumentException("Header value contains invalid characters.", nameof(value));
        }
    }

    private static bool IsTokenChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    private static bool IsValueChar(char c) => c == '\t' || (c >= ' ' && c != '\u007F');

    private static bool IsReserved(ReadOnlySpan<byte> name) =>
        Ascii.EqualsIgnoreCase(name, "Content-Length"u8) ||
        Ascii.EqualsIgnoreCase(name, "Content-Type"u8) ||
        Ascii.EqualsIgnoreCase(name, "Connection"u8) ||
        Ascii.EqualsIgnoreCase(name, "Transfer-Encoding"u8) ||
        Ascii.EqualsIgnoreCase(name, "Date"u8) ||
        Ascii.EqualsIgnoreCase(name, "Server"u8);
}
