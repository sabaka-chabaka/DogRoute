using DogRoute.Server.Tests.Support;

namespace DogRoute.Server.Tests;

public class ServerBodyTests
{
    private const string ChunkedHead = "POST /echo HTTP/1.1\r\nHost: t\r\nTransfer-Encoding: chunked\r\n\r\n";
    private const string ExpectHead = "POST /echo HTTP/1.1\r\nHost: t\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n";

    private static ValueTask Echo(DogRequest req, DogResponse res)
    {
        res.Write(req.Method == DogMethod.Post ? req.Body : req.Path);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Chunked_body_is_decoded()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync(ChunkedHead + "5\r\nhello\r\n5;ext=1\r\n dog!\r\n0\r\nX-Trailer: v\r\n\r\n");

        Assert.Equal("hello dog!", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Chunked_body_arriving_in_pieces_is_decoded()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        string[] pieces = [ChunkedHead, "5\r\nhel", "lo\r\n6\r\n wor", "ld\r\n0\r\n", "\r\n"];
        foreach (string piece in pieces)
        {
            await client.SendAsync(piece);
            await Task.Delay(50);
        }

        Assert.Equal("hello world", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Empty_chunked_body_gives_an_empty_echo()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync(ChunkedHead + "0\r\n\r\n");
        HttpReply reply = await client.ReadReplyAsync();

        Assert.Equal("HTTP/1.1 200 OK", reply.StatusLine);
        Assert.Equal("", reply.Body);
    }

    [Fact]
    public async Task Request_after_a_chunked_body_is_served()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync(ChunkedHead + "5\r\nhello\r\n0\r\n\r\n" + "GET /next HTTP/1.1\r\nHost: t\r\n\r\n");

        Assert.Equal("hello", (await client.ReadReplyAsync()).Body);
        Assert.Equal("/next", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Malformed_chunk_gets_400_and_connection_closes()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync(ChunkedHead + "zz\r\n");

        Assert.StartsWith("HTTP/1.1 400 ", (await client.ReadReplyAsync()).StatusLine);
        Assert.True(await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Too_large_chunked_body_gets_413()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync(ChunkedHead + "100001\r\n");

        Assert.StartsWith("HTTP/1.1 413 ", (await client.ReadReplyAsync()).StatusLine);
        Assert.True(await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Expect_continue_gets_100_then_the_final_response()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync(ExpectHead);
        Assert.Equal("HTTP/1.1 100 Continue", (await client.ReadReplyAsync()).StatusLine);

        await client.SendAsync("hello");
        HttpReply final = await client.ReadReplyAsync();

        Assert.Equal("HTTP/1.1 200 OK", final.StatusLine);
        Assert.Equal("hello", final.Body);
    }

    [Fact]
    public async Task Expect_continue_is_skipped_when_the_body_is_already_there()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync(ExpectHead + "hello");
        HttpReply first = await client.ReadReplyAsync();

        Assert.Equal("HTTP/1.1 200 OK", first.StatusLine);
        Assert.Equal("hello", first.Body);
    }

    [Fact]
    public async Task Expect_continue_is_sent_only_once()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync(ExpectHead);
        Assert.Equal("HTTP/1.1 100 Continue", (await client.ReadReplyAsync()).StatusLine);

        await client.SendAsync("he");
        await Task.Delay(150);
        await client.SendAsync("llo");

        HttpReply final = await client.ReadReplyAsync();
        Assert.Equal("HTTP/1.1 200 OK", final.StatusLine);
        Assert.Equal("hello", final.Body);
    }

    [Fact]
    public async Task Expect_continue_works_with_chunked_bodies()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync(
            "POST /echo HTTP/1.1\r\nHost: t\r\nTransfer-Encoding: chunked\r\nExpect: 100-continue\r\n\r\n");
        Assert.Equal("HTTP/1.1 100 Continue", (await client.ReadReplyAsync()).StatusLine);

        await client.SendAsync("5\r\nhello\r\n0\r\n\r\n");

        Assert.Equal("hello", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Oversized_content_length_is_rejected_without_100()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync(
            "POST /echo HTTP/1.1\r\nHost: t\r\nContent-Length: 2000000\r\nExpect: 100-continue\r\n\r\n");
        HttpReply first = await client.ReadReplyAsync();

        Assert.StartsWith("HTTP/1.1 413 ", first.StatusLine);
        Assert.True(await client.WaitForCloseAsync());
    }
}
