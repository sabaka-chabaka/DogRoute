using DogRoute.Server.Tests.Support;

namespace DogRoute.Server.Tests;

public class ServerHeadersTests
{
    [Fact]
    public async Task Custom_headers_reach_the_client()
    {
        await using var server = new TestServer((req, res) =>
        {
            res.Headers.Add("X-Dog"u8, "woof"u8);
            res.Headers.Add("X-Name", "Rex");
            res.Write("ok"u8);
            return ValueTask.CompletedTask;
        });
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        HttpReply reply = await client.ReadReplyAsync();

        Assert.Equal("woof", reply.Headers["X-Dog"]);
        Assert.Equal("Rex", reply.Headers["X-Name"]);
        Assert.Equal("ok", reply.Body);
    }

    [Fact]
    public async Task Redirect_reaches_the_client()
    {
        await using var server = new TestServer((req, res) =>
        {
            res.Redirect("/new");
            return ValueTask.CompletedTask;
        });
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        HttpReply reply = await client.ReadReplyAsync();

        Assert.Equal("HTTP/1.1 302 Found", reply.StatusLine);
        Assert.Equal("/new", reply.Headers["Location"]);
    }

    [Fact]
    public async Task Headers_do_not_leak_into_the_next_response()
    {
        await using var server = new TestServer((req, res) =>
        {
            if (req.Path.SequenceEqual("/a"u8))
                res.Headers.Add("X-Only-A", "1");

            res.Write("ok"u8);
            return ValueTask.CompletedTask;
        });
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET /a HTTP/1.1\r\nHost: t\r\n\r\nGET /b HTTP/1.1\r\nHost: t\r\n\r\n");
        HttpReply a = await client.ReadReplyAsync();
        HttpReply b = await client.ReadReplyAsync();

        Assert.True(a.Headers.ContainsKey("X-Only-A"));
        Assert.False(b.Headers.ContainsKey("X-Only-A"));
    }

    [Fact]
    public async Task Invalid_header_in_handler_gives_500_and_connection_survives()
    {
        await using var server = new TestServer((req, res) =>
        {
            if (req.Path.SequenceEqual("/bad"u8))
                res.Headers.Add("Content-Length", "5");

            res.Write("ok"u8);
            return ValueTask.CompletedTask;
        });
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET /bad HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("HTTP/1.1 500 Internal Server Error", (await client.ReadReplyAsync()).StatusLine);

        await client.SendAsync("GET /good HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("ok", (await client.ReadReplyAsync()).Body);
    }
}
