using System.Buffers;
using System.Buffers.Text;

namespace DogRoute.Server;

internal static class HttpWriter
{
    public static void Write(ByteBuffer o, DogResponse r, bool keepAlive, bool isHead)
    {
        int status = r.StatusCode;

        ReadOnlySpan<byte> statusLine = StatusLine(status);
        if (statusLine.IsEmpty)
        {
            o.Write("HTTP/1.1 "u8);
            WriteInt(o, status);
            o.Write(" Unknown\r\n"u8);
        }
        else
        {
            o.Write(statusLine);
        }

        o.Write("Server: DogRoute\r\n"u8);
        o.Write(DateHeader.Get());
        o.Write(r.Headers.Raw);

        bool bodyAllowed = status >= 200 && status != 204 && status != 304;
        if (bodyAllowed)
        {
            o.Write("Content-Type: "u8);
            o.Write(r.ContentType);
            o.Write("\r\nContent-Length: "u8);
            WriteInt(o, r.BodyLength);
            o.Write("\r\n"u8);
        }

        o.Write(keepAlive ? "Connection: keep-alive\r\n\r\n"u8 : "Connection: close\r\n\r\n"u8);

        if (bodyAllowed && !isHead)
            o.Write(r.BodySpan);
    }

    private static void WriteInt(ByteBuffer o, int value)
    {
        Utf8Formatter.TryFormat(value, o.GetSpan(11), out int written);
        o.Advance(written);
    }

    private static ReadOnlySpan<byte> StatusLine(int code) => code switch
    {
        200 => "HTTP/1.1 200 OK\r\n"u8,
        201 => "HTTP/1.1 201 Created\r\n"u8,
        204 => "HTTP/1.1 204 No Content\r\n"u8,
        301 => "HTTP/1.1 301 Moved Permanently\r\n"u8,
        302 => "HTTP/1.1 302 Found\r\n"u8,
        303 => "HTTP/1.1 303 See Other\r\n"u8,
        304 => "HTTP/1.1 304 Not Modified\r\n"u8,
        307 => "HTTP/1.1 307 Temporary Redirect\r\n"u8,
        308 => "HTTP/1.1 308 Permanent Redirect\r\n"u8,
        400 => "HTTP/1.1 400 Bad Request\r\n"u8,
        401 => "HTTP/1.1 401 Unauthorized\r\n"u8,
        403 => "HTTP/1.1 403 Forbidden\r\n"u8,
        404 => "HTTP/1.1 404 Not Found\r\n"u8,
        405 => "HTTP/1.1 405 Method Not Allowed\r\n"u8,
        413 => "HTTP/1.1 413 Content Too Large\r\n"u8,
        500 => "HTTP/1.1 500 Internal Server Error\r\n"u8,
        501 => "HTTP/1.1 501 Not Implemented\r\n"u8,
        503 => "HTTP/1.1 503 Service Unavailable\r\n"u8,
        _ => default
    };
}

internal static class DateHeader
{
    private static byte[] _value = Build();
    private static long _stamp = Environment.TickCount64;

    public static byte[] Get()
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _stamp) >= 1000)
        {
            Volatile.Write(ref _value, Build());
            Volatile.Write(ref _stamp, now);
        }
        return Volatile.Read(ref _value);
    }

    private static byte[] Build()
    {
        var b = new byte[37];
        "Date: "u8.CopyTo(b);
        Utf8Formatter.TryFormat(DateTime.UtcNow, b.AsSpan(6), out int written, new StandardFormat('R'));
        "\r\n"u8.CopyTo(b.AsSpan(6 + written));
        return b;
    }
}
