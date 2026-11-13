using DogRoute.Server.Tests.Support;

namespace DogRoute.Server.Tests;

public class ServerErrorTests
{
    private static ValueTask EchoDecodedPath(DogRequest req, DogResponse res)
    {
        res.Write(req.DecodedPath);
        return ValueTask.CompletedTask;
    }

    private static ValueTask EchoRawPath(DogRequest req, DogResponse res)
    {
        res.Write(req.Path);
        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("GARBAGE\r\n\r\n", 400)]
    [InlineData("GET / HTTP/1.1\r\n\r\n", 400)]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n", 400)]
    [InlineData("GET / HTTP/2.0\r\nHost: t\r\n\r\n", 505)]
    [InlineData("POST / HTTP/1.1\r\nHost: t\r\nTransfer-Encoding: gzip\r\n\r\n", 501)]
    [InlineData("POST / HTTP/1.1\r\nHost: t\r\nTransfer-Encoding: chunked\r\nContent-Length: 3\r\n\r\n", 400)]
    [InlineData("POST / HTTP/1.1\r\nHost: t\r\nContent-Length: 2000000\r\n\r\n", 413)]
    [InlineData("GET / HTTP/1.1\r\nHost: t\r\nExpect: magic\r\n\r\n", 417)]
    [InlineData("GET /../etc HTTP/1.1\r\nHost: t\r\n\r\n", 400)]
    [InlineData("GET /%zz HTTP/1.1\r\nHost: t\r\n\r\n", 400)]
    public async Task Rejected_requests_get_the_right_status_and_the_connection_closes(string raw, int expectedStatus)
    {
        await using var server = new TestServer(EchoDecodedPath);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync(raw);
        HttpReply reply = await client.ReadReplyAsync();

        Assert.StartsWith($"HTTP/1.1 {expectedStatus} ", reply.StatusLine);
        Assert.Equal("close", reply.Headers["Connection"]);
        Assert.True(await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Oversized_header_block_gets_431()
    {
        await using var server = new TestServer(EchoDecodedPath);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\nX: " + new string('a', 20000) + "\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 431 ", (await client.ReadReplyAsync()).StatusLine);
        Assert.True(await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Too_many_headers_get_431()
    {
        await using var server = new TestServer(EchoDecodedPath);
        using var client = await RawClient.ConnectAsync(server.Port);

        string headers = string.Concat(Enumerable.Range(0, HttpParser.MaxHeaders).Select(i => $"H{i}: v\r\n"));
        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\n" + headers + "\r\n");

        Assert.StartsWith("HTTP/1.1 431 ", (await client.ReadReplyAsync()).StatusLine);
        Assert.True(await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Http10_request_without_host_is_served_and_closed()
    {
        await using var server = new TestServer(EchoDecodedPath);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET /old HTTP/1.0\r\n\r\n");
        HttpReply reply = await client.ReadReplyAsync();

        Assert.Equal("HTTP/1.1 200 OK", reply.StatusLine);
        Assert.Equal("/old", reply.Body);
        Assert.Equal("close", reply.Headers["Connection"]);
        Assert.True(await client.WaitForCloseAsync());
    }

    [Theory]
    [InlineData("/hello%20world", "/hello world")]
    [InlineData("/a%20b/../c", "/c")]
    [InlineData("/a/./b", "/a/b")]
    [InlineData("/plain/path", "/plain/path")]
    [InlineData("/caf%C3%A9", "/caf\u00e9")]
    public async Task Path_is_decoded_and_normalized(string target, string expected)
    {
        await using var server = new TestServer(EchoDecodedPath);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync($"GET {target} HTTP/1.1\r\nHost: t\r\n\r\n");

        Assert.Equal(expected, (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Raw_path_is_still_available()
    {
        await using var server = new TestServer(EchoRawPath);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET /a%20b HTTP/1.1\r\nHost: t\r\n\r\n");

        Assert.Equal("/a%20b", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Decoded_path_does_not_leak_into_the_next_request()
    {
        await using var server = new TestServer(EchoDecodedPath);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync(
            "GET /a%20b HTTP/1.1\r\nHost: t\r\n\r\n" +
            "GET /plain HTTP/1.1\r\nHost: t\r\n\r\n");

        Assert.Equal("/a b", (await client.ReadReplyAsync()).Body);
        Assert.Equal("/plain", (await client.ReadReplyAsync()).Body);
    }
}
