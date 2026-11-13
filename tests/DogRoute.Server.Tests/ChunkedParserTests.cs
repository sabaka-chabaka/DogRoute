using System.Buffers;
using System.Text;
using DogRoute.Server.Tests.Support;

namespace DogRoute.Server.Tests;

public class ChunkedParserTests
{
    private const string Head = "POST /up HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n";

    private static string S(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    private static long Left(in ReadOnlySequence<byte> buf, SequencePosition consumed) => buf.Slice(consumed).Length;

    private static ParseStatus Parse(string raw, DogRequest req) => HttpParser.TryParse(Seq.Of(raw), req, out _);

    [Fact]
    public void Decodes_single_chunk()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.Ok, Parse(Head + "5\r\nhello\r\n0\r\n\r\n", req));
        Assert.Equal("hello", S(req.Body));
    }

    [Fact]
    public void Decodes_multiple_chunks()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.Ok, Parse(Head + "5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n", req));
        Assert.Equal("hello world", S(req.Body));
    }

    [Theory]
    [InlineData("A\r\n0123456789\r\n0\r\n\r\n", "0123456789")]
    [InlineData("a\r\n0123456789\r\n0\r\n\r\n", "0123456789")]
    [InlineData("0000005\r\nhello\r\n0\r\n\r\n", "hello")]
    public void Parses_hex_chunk_sizes(string chunks, string expected)
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.Ok, Parse(Head + chunks, req));
        Assert.Equal(expected, S(req.Body));
    }

    [Fact]
    public void Ignores_chunk_extensions()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.Ok, Parse(Head + "5;name=value\r\nhello\r\n4 ;x\r\n dog\r\n0;last\r\n\r\n", req));
        Assert.Equal("hello dog", S(req.Body));
    }

    [Fact]
    public void Skips_trailers()
    {
        using var req = new DogRequest();
        var buf = Seq.Of(Head + "5\r\nhello\r\n0\r\nX-Sum: 1\r\nX-Other: 2\r\n\r\n");

        Assert.Equal(ParseStatus.Ok, HttpParser.TryParse(buf, req, out var consumed));
        Assert.Equal("hello", S(req.Body));
        Assert.Equal(0, Left(buf, consumed));
    }

    [Fact]
    public void Empty_chunked_body_is_ok()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.Ok, Parse(Head + "0\r\n\r\n", req));
        Assert.True(req.Body.IsEmpty);
    }

    [Fact]
    public void Every_proper_prefix_needs_more()
    {
        string full = Head + "5\r\nhello\r\n6;x=1\r\n world\r\n0\r\nX-T: v\r\n\r\n";
        using var req = new DogRequest();

        for (int i = Head.Length; i < full.Length; i++)
        {
            ParseStatus status = Parse(full[..i], req);
            Assert.True(status == ParseStatus.NeedMoreBody, $"prefix of {i} bytes gave {status}");
        }

        Assert.Equal(ParseStatus.Ok, Parse(full, req));
        Assert.Equal("hello world", S(req.Body));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void Decodes_across_segment_boundaries(int segmentSize)
    {
        using var req = new DogRequest();
        var buf = Seq.Segmented(Head + "5\r\nhello\r\n6;x=1\r\n world\r\n0\r\nX-T: v\r\n\r\n", segmentSize);
        Assert.False(buf.IsSingleSegment);

        Assert.Equal(ParseStatus.Ok, HttpParser.TryParse(buf, req, out var consumed));
        Assert.Equal("hello world", S(req.Body));
        Assert.Equal(0, Left(buf, consumed));
    }

    [Theory]
    [InlineData("zz\r\nhello\r\n0\r\n\r\n")]
    [InlineData("\r\nhello\r\n0\r\n\r\n")]
    [InlineData("-5\r\nhello\r\n0\r\n\r\n")]
    [InlineData("5\r\nhelloXX0\r\n\r\n")]
    [InlineData("5\r\nhello\n0\r\n\r\n")]
    public void Bad_framing_is_bad_request(string chunks)
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.BadRequest, Parse(Head + chunks, req));
    }

    [Fact]
    public void Chunk_size_line_that_is_too_long_is_bad_request()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.BadRequest, Parse(Head + "5;" + new string('x', 300) + "\r\nhello\r\n0\r\n\r\n", req));
    }

    [Theory]
    [InlineData("100001\r\n")]
    [InlineData("FFFFFFFFFFFFFFFFFF\r\n")]
    public void Chunk_bigger_than_the_body_limit_is_payload_too_large(string chunks)
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.PayloadTooLarge, Parse(Head + chunks, req));
    }

    [Fact]
    public void Chunks_adding_up_over_the_body_limit_are_payload_too_large()
    {
        string half = new('a', HttpParser.MaxBodySize / 2);
        string raw = Head + "80000\r\n" + half + "\r\n" + "80000\r\n" + half + "\r\n" + "1\r\nb\r\n0\r\n\r\n";
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.PayloadTooLarge, Parse(raw, req));
    }

    [Fact]
    public void Tiny_chunks_cannot_blow_past_the_wire_limit()
    {
        string raw = Head + string.Concat(Enumerable.Repeat("1\r\na\r\n", 400_000)) + "0\r\n\r\n";
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.PayloadTooLarge, Parse(raw, req));
    }

    [Fact]
    public void Oversized_trailers_are_headers_too_large()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.HeadersTooLarge, Parse(Head + "0\r\n" + new string('x', 9000), req));
    }

    [Fact]
    public void Request_after_a_chunked_body_is_parsed_next()
    {
        using var req = new DogRequest();
        var buf = Seq.Of(Head + "5\r\nhello\r\n0\r\n\r\n" + "GET /next HTTP/1.1\r\nHost: x\r\n\r\n");

        Assert.Equal(ParseStatus.Ok, HttpParser.TryParse(buf, req, out var end1));
        Assert.Equal("hello", S(req.Body));

        var rest = buf.Slice(end1);
        Assert.Equal(ParseStatus.Ok, HttpParser.TryParse(rest, req, out var end2));
        Assert.Equal("/next", S(req.Path));
        Assert.Equal(0, Left(rest, end2));
    }
}
