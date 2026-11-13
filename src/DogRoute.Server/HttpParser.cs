using System.Buffers;
using System.Text;

namespace DogRoute.Server;

internal enum ParseStatus
{
    NeedMore,
    NeedMoreBody,
    Ok,
    BadRequest,
    PayloadTooLarge,
    HeadersTooLarge,
    ExpectationFailed,
    NotImplemented,
    VersionNotSupported,
}

internal static class HttpParser
{
    public const int MaxHeadSize = 16 * 1024;
    public const int MaxBodySize = 1024 * 1024;
    public const int MaxHeaders = 64;

    public const int MaxRequestWireSize = MaxHeadSize + 2 * MaxBodySize + 16 * 1024;

    private const int MaxChunkLine = 256;
    private const int MaxTrailerSize = 8 * 1024;
    private const int ChunkSizeInvalid = -1;
    private const int ChunkSizeTooLarge = -2;

    private enum BodyKind { None, Length, Chunked }

    public static ParseStatus TryParse(in ReadOnlySequence<byte> buffer, DogRequest req, out SequencePosition consumed)
    {
        consumed = buffer.Start;
        req.Reset();

        var reader = new SequenceReader<byte>(buffer);

        if (!reader.TryReadTo(out ReadOnlySequence<byte> head, "\r\n\r\n"u8))
            return buffer.Length > MaxHeadSize ? ParseStatus.HeadersTooLarge : ParseStatus.NeedMore;

        if (head.Length > MaxHeadSize) return ParseStatus.HeadersTooLarge;

        req._head = head.IsSingleSegment ? head.First : req.CopyHead(head);

        ParseStatus headStatus = ParseHead(req, out BodyKind bodyKind, out int contentLength);
        if (headStatus != ParseStatus.Ok) return headStatus;

        if (bodyKind == BodyKind.Length)
        {
            if (reader.Remaining < contentLength) return ParseStatus.NeedMoreBody;

            ReadOnlySequence<byte> body = reader.UnreadSequence.Slice(0, contentLength);
            req._body = body.IsSingleSegment ? body.First : req.CopyBody(body);
            reader.Advance(contentLength);
        }
        else if (bodyKind == BodyKind.Chunked)
        {
            ParseStatus bodyStatus = ReadChunkedBody(ref reader, req);
            if (bodyStatus != ParseStatus.Ok) return bodyStatus;
        }

        consumed = reader.Position;
        return ParseStatus.Ok;
    }

    private static ParseStatus ParseHead(DogRequest req, out BodyKind bodyKind, out int contentLength)
    {
        bodyKind = BodyKind.None;
        contentLength = 0;
        ReadOnlySpan<byte> span = req._head.Span;

        int lineEnd = span.IndexOf("\r\n"u8);
        ReadOnlySpan<byte> line = lineEnd < 0 ? span : span[..lineEnd];

        int sp1 = line.IndexOf((byte)' ');
        if (sp1 <= 0) return ParseStatus.BadRequest;

        int sp2 = line[(sp1 + 1)..].IndexOf((byte)' ');
        if (sp2 <= 0) return ParseStatus.BadRequest;
        sp2 += sp1 + 1;

        ReadOnlySpan<byte> method = line[..sp1];
        ReadOnlySpan<byte> target = line[(sp1 + 1)..sp2];
        ReadOnlySpan<byte> version = line[(sp2 + 1)..];

        if (version.SequenceEqual("HTTP/1.1"u8)) req.IsHttp10 = false;
        else if (version.SequenceEqual("HTTP/1.0"u8)) req.IsHttp10 = true;
        else return LooksLikeHttpVersion(version) ? ParseStatus.VersionNotSupported : ParseStatus.BadRequest;

        if (target.IndexOfAnyExceptInRange((byte)0x21, (byte)0x7E) >= 0) return ParseStatus.BadRequest;
        if (target[0] != (byte)'/' && !(target.Length == 1 && target[0] == (byte)'*'))
            return ParseStatus.BadRequest;

        req.Method = ParseMethod(method);
        req._methodLength = method.Length;
        req._targetStart = sp1 + 1;
        req._targetLength = target.Length;

        int q = target.IndexOf((byte)'?');
        req._pathLength = q < 0 ? target.Length : q;

        if (!req.NormalizePath()) return ParseStatus.BadRequest;

        bool seenLength = false, seenTransferEncoding = false, chunked = false;
        bool connClose = false, connKeepAlive = false;
        int hostCount = 0;
        int pos = lineEnd < 0 ? span.Length : lineEnd + 2;

        while (pos < span.Length)
        {
            ReadOnlySpan<byte> rest = span[pos..];
            int end = rest.IndexOf("\r\n"u8);
            ReadOnlySpan<byte> h = end < 0 ? rest : rest[..end];

            int colon = h.IndexOf((byte)':');
            if (colon <= 0) return ParseStatus.BadRequest;

            ReadOnlySpan<byte> name = h[..colon];
            if (name.IndexOfAny((byte)' ', (byte)'\t') >= 0) return ParseStatus.BadRequest;

            int vs = colon + 1, ve = h.Length;
            while (vs < ve && (h[vs] == ' ' || h[vs] == '\t')) vs++;
            while (ve > vs && (h[ve - 1] == ' ' || h[ve - 1] == '\t')) ve--;
            ReadOnlySpan<byte> value = h[vs..ve];

            if (req._headerCount == MaxHeaders) return ParseStatus.HeadersTooLarge;
            req._headers[req._headerCount++] = new DogRequest.HeaderEntry
            {
                NameStart = pos,
                NameLength = name.Length,
                ValueStart = pos + vs,
                ValueLength = value.Length
            };

            if (Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
            {
                if (seenLength) return ParseStatus.BadRequest;

                int lengthResult = TryParseLength(value, out contentLength);
                if (lengthResult == 1) return ParseStatus.BadRequest;
                if (lengthResult == 2 || contentLength > MaxBodySize) return ParseStatus.PayloadTooLarge;

                seenLength = true;
            }
            else if (Ascii.EqualsIgnoreCase(name, "Transfer-Encoding"u8))
            {
                if (seenTransferEncoding) return ParseStatus.BadRequest;
                seenTransferEncoding = true;

                if (!Ascii.EqualsIgnoreCase(value, "chunked"u8)) return ParseStatus.NotImplemented;
                chunked = true;
            }
            else if (Ascii.EqualsIgnoreCase(name, "Connection"u8))
            {
                if (Ascii.EqualsIgnoreCase(value, "close"u8)) connClose = true;
                else if (Ascii.EqualsIgnoreCase(value, "keep-alive"u8)) connKeepAlive = true;
            }
            else if (Ascii.EqualsIgnoreCase(name, "Host"u8))
            {
                hostCount++;
            }
            else if (Ascii.EqualsIgnoreCase(name, "Expect"u8))
            {
                if (!Ascii.EqualsIgnoreCase(value, "100-continue"u8)) return ParseStatus.ExpectationFailed;
                req.ExpectContinue = !req.IsHttp10;
            }

            pos += h.Length + 2;
        }

        if (!req.IsHttp10 && hostCount != 1) return ParseStatus.BadRequest;
        if (chunked && (seenLength || req.IsHttp10)) return ParseStatus.BadRequest;

        req.KeepAlive = req.IsHttp10 ? connKeepAlive : !connClose;
        bodyKind = chunked ? BodyKind.Chunked : contentLength > 0 ? BodyKind.Length : BodyKind.None;
        return ParseStatus.Ok;
    }

    private static ParseStatus ReadChunkedBody(ref SequenceReader<byte> reader, DogRequest req)
    {
        SequenceReader<byte> scan = reader;
        ParseStatus status = ScanChunks(ref scan, Span<byte>.Empty, write: false, out int total);
        if (status != ParseStatus.Ok) return status;

        if (total > 0)
        {
            Memory<byte> dest = req.AllocBody(total);
            SequenceReader<byte> second = reader;
            ScanChunks(ref second, dest.Span, write: true, out _);
            req._body = dest;
        }

        reader = scan;
        return ParseStatus.Ok;
    }

    private static ParseStatus ScanChunks(ref SequenceReader<byte> reader, Span<byte> dest, bool write, out int total)
    {
        total = 0;
        Span<byte> line = stackalloc byte[MaxChunkLine];

        while (true)
        {
            if (reader.Consumed > MaxRequestWireSize) return ParseStatus.PayloadTooLarge;

            if (!reader.TryReadTo(out ReadOnlySequence<byte> sizeLine, "\r\n"u8))
                return reader.Remaining > MaxChunkLine ? ParseStatus.BadRequest : ParseStatus.NeedMoreBody;

            if (sizeLine.Length > MaxChunkLine) return ParseStatus.BadRequest;

            int lineLength = (int)sizeLine.Length;
            sizeLine.CopyTo(line);

            int size = ParseChunkSize(line[..lineLength]);
            if (size == ChunkSizeInvalid) return ParseStatus.BadRequest;
            if (size == ChunkSizeTooLarge) return ParseStatus.PayloadTooLarge;

            if (size == 0) return SkipTrailers(ref reader);

            if ((long)total + size > MaxBodySize) return ParseStatus.PayloadTooLarge;
            if (reader.Remaining < (long)size + 2) return ParseStatus.NeedMoreBody;

            if (write)
                reader.UnreadSequence.Slice(0, size).CopyTo(dest.Slice(total, size));

            reader.Advance(size);
            if (!reader.IsNext("\r\n"u8, advancePast: true)) return ParseStatus.BadRequest;

            total += size;
        }
    }

    private static ParseStatus SkipTrailers(ref SequenceReader<byte> reader)
    {
        int trailerBytes = 0;

        while (true)
        {
            if (!reader.TryReadTo(out ReadOnlySequence<byte> trailer, "\r\n"u8))
                return reader.Remaining > MaxTrailerSize ? ParseStatus.HeadersTooLarge : ParseStatus.NeedMoreBody;

            if (trailer.IsEmpty) return ParseStatus.Ok;

            trailerBytes += (int)trailer.Length + 2;
            if (trailerBytes > MaxTrailerSize) return ParseStatus.HeadersTooLarge;
        }
    }

    private static int ParseChunkSize(ReadOnlySpan<byte> line)
    {
        int i = 0;
        long value = 0;

        while (i < line.Length)
        {
            int hex = UriPath.HexValue(line[i]);
            if (hex < 0) break;

            value = (value << 4) | (long)hex;
            if (value > MaxBodySize) return ChunkSizeTooLarge;
            i++;
        }

        if (i == 0) return ChunkSizeInvalid;

        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        if (i < line.Length && line[i] != (byte)';') return ChunkSizeInvalid;

        return (int)value;
    }

    private static bool LooksLikeHttpVersion(ReadOnlySpan<byte> v) =>
        v.Length == 8 &&
        v.StartsWith("HTTP/"u8) &&
        v[5] is >= (byte)'0' and <= (byte)'9' &&
        v[6] == (byte)'.' &&
        v[7] is >= (byte)'0' and <= (byte)'9';

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

    private static int TryParseLength(ReadOnlySpan<byte> v, out int result)
    {
        result = 0;
        if (v.IsEmpty) return 1;

        foreach (byte b in v)
        {
            if (b < (byte)'0' || b > (byte)'9') return 1;
        }

        ReadOnlySpan<byte> digits = v.TrimStart((byte)'0');
        if (digits.Length > 9) return 2;

        foreach (byte b in digits)
            result = result * 10 + (b - '0');

        return 0;
    }
}
