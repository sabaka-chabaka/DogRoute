using System.Buffers;
using System.Text;

namespace DogRoute.Server;

internal enum ParseStatus { NeedMore, Ok, Bad }

internal static class HttpParser
{
    public const int MaxHeadSize = 16 * 1024;
    public const int MaxBodySize = 1024 * 1024;
    public const int MaxHeaders = 64;

    public static ParseStatus TryParse(in ReadOnlySequence<byte> buffer, DogRequest req, out SequencePosition consumed)
    {
        consumed = buffer.Start;
        req.Reset();

        var reader = new SequenceReader<byte>(buffer);

        if (!reader.TryReadTo(out ReadOnlySequence<byte> head, "\r\n\r\n"u8))
            return buffer.Length > MaxHeadSize ? ParseStatus.Bad : ParseStatus.NeedMore;

        if (head.Length > MaxHeadSize) return ParseStatus.Bad;

        req._head = head.IsSingleSegment ? head.First : req.CopyHead(head);

        if (!ParseHead(req, out int contentLength)) return ParseStatus.Bad;

        if (contentLength > 0)
        {
            if (reader.Remaining < contentLength) return ParseStatus.NeedMore;

            ReadOnlySequence<byte> body = reader.UnreadSequence.Slice(0, contentLength);
            req._body = body.IsSingleSegment ? body.First : req.CopyBody(body);
            reader.Advance(contentLength);
        }

        consumed = reader.Position;
        return ParseStatus.Ok;
    }

    private static bool ParseHead(DogRequest req, out int contentLength)
    {
        contentLength = 0;
        ReadOnlySpan<byte> span = req._head.Span;

        int lineEnd = span.IndexOf("\r\n"u8);
        ReadOnlySpan<byte> line = lineEnd < 0 ? span : span[..lineEnd];

        int sp1 = line.IndexOf((byte)' ');
        if (sp1 <= 0) return false;

        int sp2 = line[(sp1 + 1)..].IndexOf((byte)' ');
        if (sp2 <= 0) return false;
        sp2 += sp1 + 1;

        ReadOnlySpan<byte> method = line[..sp1];
        ReadOnlySpan<byte> target = line[(sp1 + 1)..sp2];
        ReadOnlySpan<byte> version = line[(sp2 + 1)..];

        if (version.SequenceEqual("HTTP/1.1"u8)) req.IsHttp10 = false;
        else if (version.SequenceEqual("HTTP/1.0"u8)) req.IsHttp10 = true;
        else return false;

        if (target[0] != (byte)'/' && !(target.Length == 1 && target[0] == (byte)'*'))
            return false;

        req.Method = ParseMethod(method);
        req._methodLength = method.Length;
        req._targetStart = sp1 + 1;
        req._targetLength = target.Length;

        int q = target.IndexOf((byte)'?');
        req._pathLength = q < 0 ? target.Length : q;

        bool seenLength = false, connClose = false, connKeepAlive = false;
        int pos = lineEnd < 0 ? span.Length : lineEnd + 2;

        while (pos < span.Length)
        {
            ReadOnlySpan<byte> rest = span[pos..];
            int end = rest.IndexOf("\r\n"u8);
            ReadOnlySpan<byte> h = end < 0 ? rest : rest[..end];

            int colon = h.IndexOf((byte)':');
            if (colon <= 0) return false;

            ReadOnlySpan<byte> name = h[..colon];
            if (name.IndexOfAny((byte)' ', (byte)'\t') >= 0) return false;

            int vs = colon + 1, ve = h.Length;
            while (vs < ve && (h[vs] == ' ' || h[vs] == '\t')) vs++;
            while (ve > vs && (h[ve - 1] == ' ' || h[ve - 1] == '\t')) ve--;
            ReadOnlySpan<byte> value = h[vs..ve];

            if (req._headerCount == MaxHeaders) return false;
            req._headers[req._headerCount++] = new DogRequest.HeaderEntry
            {
                NameStart = pos,
                NameLength = name.Length,
                ValueStart = pos + vs,
                ValueLength = value.Length
            };

            if (Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
            {
                if (seenLength || !TryParseLength(value, out contentLength) || contentLength > MaxBodySize)
                    return false;
                seenLength = true;
            }
            else if (Ascii.EqualsIgnoreCase(name, "Transfer-Encoding"u8))
            {
                return false;
            }
            else if (Ascii.EqualsIgnoreCase(name, "Connection"u8))
            {
                if (Ascii.EqualsIgnoreCase(value, "close"u8)) connClose = true;
                else if (Ascii.EqualsIgnoreCase(value, "keep-alive"u8)) connKeepAlive = true;
            }

            pos += h.Length + 2;
        }

        req.KeepAlive = req.IsHttp10 ? connKeepAlive : !connClose;
        return true;
    }

    private static DogMethod ParseMethod(ReadOnlySpan<byte> m)
    {
        if (m.SequenceEqual("GET"u8)) return DogMethod.Get;
        if (m.SequenceEqual("POST"u8)) return DogMethod.Post;
        if (m.SequenceEqual("PUT"u8)) return DogMethod.Put;
        if (m.SequenceEqual("DELETE"u8)) return DogMethod.Delete;
        if (m.SequenceEqual("HEAD"u8)) return DogMethod.Head;
        if (m.SequenceEqual("PATCH"u8)) return DogMethod.Patch;
        if (m.SequenceEqual("OPTIONS"u8)) return DogMethod.Options;
        return DogMethod.Other;
    }

    private static bool TryParseLength(ReadOnlySpan<byte> v, out int result)
    {
        result = 0;
        if (v.IsEmpty || v.Length > 9) return false;

        foreach (byte b in v)
        {
            uint d = (uint)(b - '0');
            if (d > 9) return false;
            result = result * 10 + (int)d;
        }
        return true;
    }
}