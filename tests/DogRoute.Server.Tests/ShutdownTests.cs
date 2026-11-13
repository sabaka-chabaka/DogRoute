using System.Net.Sockets;
using DogRoute.Server.Tests.Support;

namespace DogRoute.Server.Tests;

public class ShutdownTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    private static ValueTask Hello(DogRequest req, DogResponse res)
    {
        res.Write("hi"u8);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Inflight_request_finishes_and_connection_is_closed()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new TestServer(async (req, res) =>
        {
            started.TrySetResult();
            await Task.Delay(300);
            res.Write("done"u8);
        });
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        await started.Task.WaitAsync(Limit);

        server.Stop();

        HttpReply reply = await client.ReadReplyAsync();
        Assert.Equal("done", reply.Body);
        Assert.Equal("close", reply.Headers["Connection"]);
        Assert.True(await client.WaitForCloseAsync());
        await server.Completion.WaitAsync(Limit);
    }

    [Fact]
    public async Task Idle_keep_alive_connections_are_closed_immediately()
    {
        await using var server = new TestServer(Hello);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        await client.ReadReplyAsync();

        server.Stop();

        Assert.True(await client.WaitForCloseAsync());
        await server.Completion.WaitAsync(Limit);
    }

    [Fact]
    public async Task New_connections_are_refused_after_shutdown()
    {
        await using var server = new TestServer(Hello);

        server.Stop();
        await server.Completion.WaitAsync(Limit);

        await Assert.ThrowsAnyAsync<SocketException>(() => RawClient.ConnectAsync(server.Port));
    }

    [Fact]
    public async Task Stuck_requests_are_aborted_after_shutdown_timeout()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new DogServerOptions
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(200),
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
        };
        await using var server = new TestServer(async (req, res) =>
        {
            started.TrySetResult();
            await Task.Delay(10_000); // зависший handler
        }, options);
        using var client = await RawClient.ConnectAsync(server.Port);

        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        await started.Task.WaitAsync(Limit);

        server.Stop();

        await server.Completion.WaitAsync(Limit);
        Assert.True(await client.WaitForCloseAsync());
    }
}
