using System.Buffers;
using System.Text;
using DogRoute.Server.Tests.Support;

namespace DogRoute.Server.Tests;

public class HttpParserTests
{
    private static string S(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    private static long Left(in ReadOnlySequence<byte> buf, SequencePosition consumed) => buf.Slice(consumed).Length;

    private static ParseStatus Parse(string raw, DogRequest req) => HttpParser.TryParse(Seq.Of(raw), req, out _);

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
        Assert.False(req.ExpectContinue);
        Assert.Equal(0, Left(buf, consumed));
    }

    [Fact]
    public void Query_is_empty_without_question_mark()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.Ok, Parse("GET /a HTTP/1.1\r\nHost: x\r\n\r\n", req));
        Assert.Equal("/a", S(req.Path));
        Assert.True(req.Query.IsEmpty);
    }

    [Fact]
    public void Header_lookup_is_case_insensitive_and_values_are_trimmed()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.Ok,
            Parse("GET / HTTP/1.1\r\nHoSt: example.com\r\nX-Test:   padded value  \r\n\r\n", req));
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
        var buf = Seq.Of("POST /echo HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\nhello");

        var status = HttpParser.TryParse(buf, req, out var consumed);

        Assert.Equal(ParseStatus.Ok, status);
        Assert.Equal(DogMethod.Post, req.Method);
        Assert.Equal("hello", S(req.Body));
        Assert.Equal(0, Left(buf, consumed));
    }

    [Fact]
    public void Content_length_with_leading_zeros_is_accepted()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.Ok, Parse("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 0005\r\n\r\nhello", req));
        Assert.Equal("hello", S(req.Body));
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

        Assert.Equal(ParseStatus.Ok, Parse($"{method} / HTTP/1.1\r\nHost: x\r\n\r\n", req));
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

        Assert.Equal(ParseStatus.Ok, Parse($"GET / {version}\r\nHost: x\r\n{conn}\r\n", req));
        Assert.Equal(expected, req.KeepAlive);
        Assert.Equal(version == "HTTP/1.0", req.IsHttp10);
    }

    [Fact]
    public void Http10_does_not_require_host()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.Ok, Parse("GET / HTTP/1.0\r\n\r\n", req));
    }

    [Fact]
    public void Incomplete_head_needs_more()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.NeedMore, Parse("GET / HTTP/1.1\r\nHost: x\r\n", req));
    }

    [Fact]
    public void Incomplete_body_needs_more_body()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.NeedMoreBody, Parse("POST /e HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\nabc", req));
    }

    [Fact]
    public void Oversized_head_without_terminator_is_headers_too_large()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.HeadersTooLarge,
            Parse("GET / HTTP/1.1\r\nX: " + new string('a', HttpParser.MaxHeadSize + 100), req));
    }

    [Fact]
    public void Oversized_head_with_terminator_is_headers_too_large()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.HeadersTooLarge,
            Parse("GET / HTTP/1.1\r\nHost: x\r\nX: " + new string('a', HttpParser.MaxHeadSize + 100) + "\r\n\r\n", req));
    }

    [Theory]
    [InlineData("GARBAGE\r\n\r\n")]
    [InlineData("GET /\r\n\r\n")]
    [InlineData("GET foo HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET / FTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /a\tb HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\nNoColon\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\n: empty-name\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\nBad Header: v\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: abc\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: -1\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nContent-Length:\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\nContent-Length: 5\r\n\r\nhello")]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nContent-Length: 5\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nTransfer-Encoding: chunked\r\n\r\n")]
    [InlineData("POST / HTTP/1.0\r\nTransfer-Encoding: chunked\r\n\r\n")]
    [InlineData("GET /../x HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /%zz HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /a%00b HTTP/1.1\r\nHost: x\r\n\r\n")]
    public void Malformed_requests_are_bad_request(string raw)
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.BadRequest, Parse(raw, req));
    }

    [Theory]
    [InlineData("HTTP/2.0")]
    [InlineData("HTTP/3.0")]
    [InlineData("HTTP/1.2")]
    [InlineData("HTTP/0.9")]
    public void Other_http_versions_are_not_supported(string version)
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.VersionNotSupported, Parse($"GET / {version}\r\nHost: x\r\n\r\n", req));
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("gzip, chunked")]
    [InlineData("identity")]
    public void Unsupported_transfer_encoding_is_not_implemented(string value)
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.NotImplemented,
            Parse($"POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: {value}\r\n\r\n", req));
    }

    [Theory]
    [InlineData("2000000")]
    [InlineData("99999999999")]
    public void Oversized_content_length_is_payload_too_large(string length)
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.PayloadTooLarge,
            Parse($"POST / HTTP/1.1\r\nHost: x\r\nContent-Length: {length}\r\n\r\n", req));
    }

    [Fact]
    public void Unknown_expectation_fails()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.ExpectationFailed, Parse("POST / HTTP/1.1\r\nHost: x\r\nExpect: magic\r\n\r\n", req));
    }

    [Fact]
    public void Expect_continue_is_flagged_while_the_body_is_pending()
    {
        using var req = new DogRequest();

        var status = Parse("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n", req);

        Assert.Equal(ParseStatus.NeedMoreBody, status);
        Assert.True(req.ExpectContinue);
    }

    [Fact]
    public void Expect_continue_with_complete_body_is_ok()
    {
        using var req = new DogRequest();

        var status = Parse("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\nhello", req);

        Assert.Equal(ParseStatus.Ok, status);
        Assert.Equal("hello", S(req.Body));
    }

    [Fact]
    public void Expect_continue_is_ignored_for_http10()
    {
        using var req = new DogRequest();

        Parse("POST / HTTP/1.0\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n", req);

        Assert.False(req.ExpectContinue);
    }

    [Fact]
    public void Header_count_limit_is_enforced()
    {
        static string Build(int count) =>
            "GET / HTTP/1.1\r\nHost: x\r\n" +
            string.Concat(Enumerable.Range(0, count).Select(i => $"H{i}: v\r\n")) + "\r\n";

        using var req = new DogRequest();

        // Host тоже заголовок, поэтому MaxHeaders - 1 дополнительных ещё влезают.
        Assert.Equal(ParseStatus.Ok, Parse(Build(HttpParser.MaxHeaders - 1), req));
        Assert.Equal(HttpParser.MaxHeaders, req.HeaderCount);

        Assert.Equal(ParseStatus.HeadersTooLarge, Parse(Build(HttpParser.MaxHeaders), req));
    }

    [Fact]
    public void Pipelined_requests_are_consumed_one_by_one()
    {
        using var req = new DogRequest();
        var buf = Seq.Of(
            "GET /1 HTTP/1.1\r\nHost: x\r\n\r\nPOST /2 HTTP/1.1\r\nHost: x\r\nContent-Length: 2\r\n\r\nhi");

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

    [Fact]
    public void Decoded_path_equals_path_when_nothing_to_decode()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.Ok, Parse("GET /plain/path.txt?q=1 HTTP/1.1\r\nHost: x\r\n\r\n", req));
        Assert.Equal("/plain/path.txt", S(req.DecodedPath));
        Assert.True(req.DecodedPath.SequenceEqual(req.Path));
    }

    [Theory]
    [InlineData("/hello%20world", "/hello world")]
    [InlineData("/a%20b/../c", "/c")]
    [InlineData("/a/./b", "/a/b")]
    [InlineData("/caf%C3%A9?x=1", "/caf\u00e9")]
    public void Decoded_path_is_decoded_and_normalized(string target, string expected)
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.Ok, Parse($"GET {target} HTTP/1.1\r\nHost: x\r\n\r\n", req));
        Assert.Equal(expected, S(req.DecodedPath));
    }

    [Fact]
    public void Raw_path_stays_raw()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.Ok, Parse("GET /a%20b HTTP/1.1\r\nHost: x\r\n\r\n", req));
        Assert.Equal("/a%20b", S(req.Path));
        Assert.Equal("/a b", S(req.DecodedPath));
    }

    [Fact]
    public void Decoded_path_state_is_reset_between_requests()
    {
        using var req = new DogRequest();

        Assert.Equal(ParseStatus.Ok, Parse("GET /a%20b HTTP/1.1\r\nHost: x\r\n\r\n", req));
        Assert.Equal("/a b", S(req.DecodedPath));

        Assert.Equal(ParseStatus.Ok, Parse("GET /plain HTTP/1.1\r\nHost: x\r\n\r\n", req));
        Assert.Equal("/plain", S(req.DecodedPath));
    }
}
