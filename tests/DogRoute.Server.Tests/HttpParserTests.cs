using System.Buffers;
using System.Text;
using DogRoute.Server.Tests.Support;

namespace DogRoute.Server.Tests;

public class HttpParserTests
{
    private static string S(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    private static long Left(in ReadOnlySequence<byte> buf, SequencePosition consumed) => buf.Slice(consumed).Length;

    [Fact]
    public void Parses_simple_get()
    {
        using var req = new DogRequest();
        var buf = Seq.Of("GET /dogs/42?sort=name HTTP/1.1\r\nHost: x\r\n\r\n");

        var status = HttpParser.TryParse(buf, req, out var consumed);

        Assert.Equal(ParseStatus.Ok, status);
        Assert.Equal(DogMethod.Get, req.Method);
        Assert.Equal("/dogs/42", S(req.Path));
        Assert.Equal("/dogs/42?sort=name", S(req.Target));
        Assert.Equal("sort=name", S(req.Query));
        Assert.True(req.KeepAlive);
        Assert.False(req.IsHttp10);
        Assert.Equal(0, Left(buf, consumed));
    }

    [Fact]
    public void Query_is_empty_without_question_mark()
    {
        using var req = new DogRequest();
        var buf = Seq.Of("GET /a HTTP/1.1\r\n\r\n");

        Assert.Equal(ParseStatus.Ok, HttpParser.TryParse(buf, req, out _));
        Assert.Equal("/a", S(req.Path));
        Assert.True(req.Query.IsEmpty);
    }

    [Fact]
    public void Header_lookup_is_case_insensitive_and_values_are_trimmed()
    {
        using var req = new DogRequest();
        var buf = Seq.Of("GET / HTTP/1.1\r\nHoSt: example.com\r\nX-Test:   padded value  \r\n\r\n");

        Assert.Equal(ParseStatus.Ok, HttpParser.TryParse(buf, req, out _));
        Assert.Equal(2, req.HeaderCount);

        Assert.True(req.TryGetHeader("host"u8, out var host));
        Assert.Equal("example.com", S(host));

        Assert.True(req.TryGetHeader("X-TEST"u8, out var test));
        Assert.Equal("padded value", S(test));

        Assert.False(req.TryGetHeader("missing"u8, out _));

        req.GetHeader(0, out var name0, out var value0);
        Assert.Equal("HoSt", S(name0));
        Assert.Equal("example.com", S(value0));
    }

    [Fact]
    public void Parses_body_by_content_length()
    {
        using var req = new DogRequest();
        var buf = Seq.Of("POST /echo HTTP/1.1\r\nContent-Length: 5\r\n\r\nhello");

        var status = HttpParser.TryParse(buf, req, out var consumed);

        Assert.Equal(ParseStatus.Ok, status);
        Assert.Equal(DogMethod.Post, req.Method);
        Assert.Equal("hello", S(req.Body));
        Assert.Equal(0, Left(buf, consumed));
    }

    [Theory]
    [InlineData("GET", DogMethod.Get)]
    [InlineData("POST", DogMethod.Post)]
    [InlineData("PUT", DogMethod.Put)]
    [InlineData("DELETE", DogMethod.Delete)]
    [InlineData("HEAD", DogMethod.Head)]
    [InlineData("PATCH", DogMethod.Patch)]
    [InlineData("OPTIONS", DogMethod.Options)]
    [InlineData("BREW", DogMethod.Other)]
    public void Detects_http_method(string method, DogMethod expected)
    {
        using var req = new DogRequest();
        var buf = Seq.Of($"{method} / HTTP/1.1\r\n\r\n");

        Assert.Equal(ParseStatus.Ok, HttpParser.TryParse(buf, req, out _));
        Assert.Equal(expected, req.Method);
        Assert.Equal(method, S(req.RawMethod));
    }

    [Theory]
    [InlineData("HTTP/1.1", null, true)]
    [InlineData("HTTP/1.1", "close", false)]
    [InlineData("HTTP/1.0", null, false)]
    [InlineData("HTTP/1.0", "keep-alive", true)]
    public void KeepAlive_follows_version_and_connection_header(string version, string? connection, bool expected)
    {
        using var req = new DogRequest();
        string conn = connection is null ? "" : $"Connection: {connection}\r\n";
        var buf = Seq.Of($"GET / {version}\r\n{conn}\r\n");

        Assert.Equal(ParseStatus.Ok, HttpParser.TryParse(buf, req, out _));
        Assert.Equal(expected, req.KeepAlive);
        Assert.Equal(version == "HTTP/1.0", req.IsHttp10);
    }

    [Fact]
    public void Incomplete_head_needs_more()
    {
        using var req = new DogRequest();
        var buf = Seq.Of("GET / HTTP/1.1\r\nHost: x\r\n");

        Assert.Equal(ParseStatus.NeedMore, HttpParser.TryParse(buf, req, out _));
    }

    [Fact]
    public void Incomplete_body_needs_more()
    {
        using var req = new DogRequest();
        var buf = Seq.Of("POST /e HTTP/1.1\r\nContent-Length: 5\r\n\r\nabc");

        Assert.Equal(ParseStatus.NeedMore, HttpParser.TryParse(buf, req, out _));
    }

    [Fact]
    public void Oversized_head_without_terminator_is_bad()
    {
        using var req = new DogRequest();
        var buf = Seq.Of("GET / HTTP/1.1\r\nX: " + new string('a', HttpParser.MaxHeadSize + 100));

        Assert.Equal(ParseStatus.Bad, HttpParser.TryParse(buf, req, out _));
    }

    [Theory]
    [InlineData("GARBAGE\r\n\r\n")]
    [InlineData("GET /\r\n\r\n")]
    [InlineData("GET foo HTTP/1.1\r\n\r\n")]
    [InlineData("GET / HTTP/2.0\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nNoColon\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\n: empty-name\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nBad Header: x\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nContent-Length: abc\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nContent-Length: -1\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nContent-Length: 2000000\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nContent-Length: 5\r\nContent-Length: 5\r\n\r\nhello")]
    public void Malformed_requests_are_bad(string raw)
    {
        using var req = new DogRequest();
        var buf = Seq.Of(raw);

        Assert.Equal(ParseStatus.Bad, HttpParser.TryParse(buf, req, out _));
    }

    [Fact]
    public void Header_count_limit_is_enforced()
    {
        static string Build(int count) =>
            "GET / HTTP/1.1\r\n" + string.Concat(Enumerable.Range(0, count).Select(i => $"H{i}: v\r\n")) + "\r\n";

        using var req = new DogRequest();

        Assert.Equal(ParseStatus.Ok, HttpParser.TryParse(Seq.Of(Build(HttpParser.MaxHeaders)), req, out _));
        Assert.Equal(HttpParser.MaxHeaders, req.HeaderCount);

        Assert.Equal(ParseStatus.Bad, HttpParser.TryParse(Seq.Of(Build(HttpParser.MaxHeaders + 1)), req, out _));
    }

    [Fact]
    public void Pipelined_requests_are_consumed_one_by_one()
    {
        using var req = new DogRequest();
        var buf = Seq.Of("GET /1 HTTP/1.1\r\n\r\nPOST /2 HTTP/1.1\r\nContent-Length: 2\r\n\r\nhi");

        Assert.Equal(ParseStatus.Ok, HttpParser.TryParse(buf, req, out var end1));
        Assert.Equal("/1", S(req.Path));

        var rest = buf.Slice(end1);
        Assert.Equal(ParseStatus.Ok, HttpParser.TryParse(rest, req, out var end2));
        Assert.Equal("/2", S(req.Path));
        Assert.Equal("hi", S(req.Body));
        Assert.Equal(0, Left(rest, end2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(13)]
    public void Parses_across_segment_boundaries(int chunkSize)
    {
        using var req = new DogRequest();
        var buf = Seq.Segmented(
            "POST /echo?x=1 HTTP/1.1\r\nHost: t\r\nContent-Length: 9\r\n\r\nhello dog", chunkSize);
        Assert.False(buf.IsSingleSegment);

        var status = HttpParser.TryParse(buf, req, out var consumed);

        Assert.Equal(ParseStatus.Ok, status);
        Assert.Equal("/echo", S(req.Path));
        Assert.Equal("x=1", S(req.Query));
        Assert.Equal("hello dog", S(req.Body));
        Assert.True(req.TryGetHeader("host"u8, out var host));
        Assert.Equal("t", S(host));
        Assert.Equal(0, Left(buf, consumed));
    }
}
