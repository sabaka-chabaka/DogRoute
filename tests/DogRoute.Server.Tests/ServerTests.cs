using DogRoute.Server.Tests.Support;

namespace DogRoute.Server.Tests;

public class ServerTests
{
    private static ValueTask Hello(DogRequest req, DogResponse res)
    {
        res.Write("Hello, World!"u8);
        return ValueTask.CompletedTask;
    }

    private static ValueTask Echo(DogRequest req, DogResponse res)
    {
        if (req.Path.SequenceEqual("/boom"u8)) throw new InvalidOperationException("boom");

        res.Write(req.Method == DogMethod.Post ? req.Body : req.Path);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Responds_with_hello_world()
    {
        await using var server = new TestServer(Hello);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        HttpReply reply = await client.ReadReplyAsync();

        Assert.Equal("HTTP/1.1 200 OK", reply.StatusLine);
        Assert.Equal("Hello, World!", reply.Body);
        Assert.Equal("13", reply.Headers["Content-Length"]);
        Assert.Equal("keep-alive", reply.Headers["Connection"]);
    }

    [Fact]
    public async Task Keep_alive_serves_several_requests_on_one_connection()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET /a HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("/a", (await client.ReadReplyAsync()).Body);

        await client.SendAsync("GET /b HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("/b", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Pipelined_requests_get_ordered_responses()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync(
            "GET /1 HTTP/1.1\r\nHost: t\r\n\r\n" +
            "GET /2 HTTP/1.1\r\nHost: t\r\n\r\n" +
            "GET /3 HTTP/1.1\r\nHost: t\r\n\r\n");

        Assert.Equal("/1", (await client.ReadReplyAsync()).Body);
        Assert.Equal("/2", (await client.ReadReplyAsync()).Body);
        Assert.Equal("/3", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Connection_close_closes_the_socket()
    {
        await using var server = new TestServer(Hello);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\nConnection: close\r\n\r\n");
        HttpReply reply = await client.ReadReplyAsync();

        Assert.Equal("close", reply.Headers["Connection"]);
        Assert.True(await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Post_body_is_echoed()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("POST /echo HTTP/1.1\r\nHost: t\r\nContent-Length: 9\r\n\r\nhello dog");

        Assert.Equal("hello dog", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Body_arriving_in_a_separate_packet_is_handled()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("POST /echo HTTP/1.1\r\nHost: t\r\nContent-Length: 9\r\n\r\n");
        await Task.Delay(100);
        await client.SendAsync("hello dog");

        Assert.Equal("hello dog", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Head_request_gets_no_body_and_connection_stays_usable()
    {
        await using var server = new TestServer(Hello);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("HEAD / HTTP/1.1\r\nHost: t\r\n\r\n");
        HttpReply head = await client.ReadReplyAsync(expectBody: false);
        Assert.Equal("13", head.Headers["Content-Length"]);

        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("Hello, World!", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Malformed_request_gets_400_and_connection_closes()
    {
        await using var server = new TestServer(Hello);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GARBAGE\r\n\r\n");
        HttpReply reply = await client.ReadReplyAsync();

        Assert.Equal("HTTP/1.1 400 Bad Request", reply.StatusLine);
        Assert.True(await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Handler_exception_gives_500_and_connection_survives()
    {
        await using var server = new TestServer(Echo);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET /boom HTTP/1.1\r\nHost: t\r\n\r\n");
        HttpReply reply = await client.ReadReplyAsync();
        Assert.Equal("HTTP/1.1 500 Internal Server Error", reply.StatusLine);
        Assert.Equal("Internal Server Error", reply.Body);

        await client.SendAsync("GET /ok HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("/ok", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Stopping_the_server_closes_idle_connections()
    {
        var server = new TestServer(Hello);
        using var client = await RawClient.ConnectAsync(server.Port);

        await server.DisposeAsync();

        Assert.True(await client.WaitForCloseAsync());
    }
}
