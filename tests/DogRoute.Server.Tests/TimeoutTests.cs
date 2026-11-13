using System.Diagnostics;
using DogRoute.Server.Tests.Support;

namespace DogRoute.Server.Tests;

public class TimeoutTests
{
    private static DogServerOptions Fast => new()
    {
        RequestTimeout = TimeSpan.FromMilliseconds(300),
        KeepAliveTimeout = TimeSpan.FromMilliseconds(300),
        HeartbeatInterval = TimeSpan.FromMilliseconds(50),
    };

    private static ValueTask Hello(DogRequest req, DogResponse res)
    {
        res.Write("hi"u8);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Idle_keep_alive_connection_is_closed()
    {
        await using var server = new TestServer(Hello, Fast);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        await client.ReadReplyAsync();

        var sw = Stopwatch.StartNew();
        Assert.True(await client.WaitForCloseAsync());
        Assert.True(sw.ElapsedMilliseconds >= 200, $"closed too early: {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Connection_that_never_sends_anything_is_closed()
    {
        await using var server = new TestServer(Hello, Fast);
        using var client = await RawClient.ConnectAsync(server.Port);

        Assert.True(await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Incomplete_head_is_cut_off()
    {
        await using var server = new TestServer(Hello, Fast);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\n");

        Assert.True(await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Incomplete_body_is_cut_off()
    {
        await using var server = new TestServer(Hello, Fast);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("POST / HTTP/1.1\r\nHost: t\r\nContent-Length: 10\r\n\r\nabc");

        Assert.True(await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Trickling_bytes_does_not_extend_the_deadline()
    {
        await using var server = new TestServer(Hello, Fast);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET / HTTP/1.1\r\n");

        bool writeFailed = false;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(100);
            try
            {
                await client.SendAsync($"X-{i}: v\r\n");
            }
            catch (IOException)
            {
                writeFailed = true;
                break;
            }
        }

        Assert.True(writeFailed || await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Idle_timeout_restarts_after_every_request()
    {
        await using var server = new TestServer(Hello, Fast);
        using var client = await RawClient.ConnectAsync(server.Port);

        for (int i = 0; i < 4; i++)
        {
            await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
            Assert.Equal("hi", (await client.ReadReplyAsync()).Body);
            await Task.Delay(150);
        }
    }

    [Fact]
    public async Task Slow_handler_is_not_cut_off_by_timeouts()
    {
        await using var server = new TestServer(async (req, res) =>
        {
            await Task.Delay(700);
            res.Write("slow"u8);
        }, Fast);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\n\r\n");

        Assert.Equal("slow", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Timeouts_can_be_disabled()
    {
        var options = new DogServerOptions
        {
            RequestTimeout = Timeout.InfiniteTimeSpan,
            KeepAliveTimeout = Timeout.InfiniteTimeSpan,
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
        };
        await using var server = new TestServer(Hello, options);
        using var client = await RawClient.ConnectAsync(server.Port);

        await Task.Delay(700);

        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("hi", (await client.ReadReplyAsync()).Body);
    }
}
