using DogRoute.Routing.Tests.Support;
using DogRoute.Server;

namespace DogRoute.Routing.Tests;

public class DogAppTests
{
    private static DogRouteHandler Text(string text) => ctx =>
    {
        ctx.Response.Write(text);
        return ValueTask.CompletedTask;
    };

    [Fact]
    public async Task Dispatches_to_the_matching_route()
    {
        await using var host = new TestHost(app =>
        {
            app.Map(DogMethod.Get, "/", Text("home"));
            app.Map(DogMethod.Get, "/dogs/{id:int}", static ctx =>
            {
                ctx.Response.Write("dog "u8);
                ctx.Response.Write(ctx.Route["id"u8]);
                return ValueTask.CompletedTask;
            });
        });
        using var client = await RawClient.ConnectAsync(host.Port);

        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("home", (await client.ReadReplyAsync()).Body);

        await client.SendAsync("GET /dogs/42?x=1 HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("dog 42", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Unknown_path_gets_404_and_the_connection_stays_open()
    {
        await using var host = new TestHost(app => app.Map(DogMethod.Get, "/", Text("home")));
        using var client = await RawClient.ConnectAsync(host.Port);

        await client.SendAsync("GET /nope HTTP/1.1\r\nHost: t\r\n\r\n");
        HttpReply miss = await client.ReadReplyAsync();
        Assert.Equal("HTTP/1.1 404 Not Found", miss.StatusLine);
        Assert.Equal("keep-alive", miss.Headers["Connection"]);

        await client.SendAsync("GET / HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("home", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Wrong_method_gets_405_with_allow()
    {
        await using var host = new TestHost(app =>
        {
            app.Map(DogMethod.Get, "/dogs", Text("list"));
            app.Map(DogMethod.Post, "/dogs", Text("created"));
            app.Map(DogMethod.Put, "/other", Text("put"));
        });
        using var client = await RawClient.ConnectAsync(host.Port);

        await client.SendAsync("DELETE /dogs HTTP/1.1\r\nHost: t\r\n\r\n");
        HttpReply reply = await client.ReadReplyAsync();

        Assert.Equal("HTTP/1.1 405 Method Not Allowed", reply.StatusLine);
        Assert.Equal("GET, HEAD, POST", reply.Headers["Allow"]);

        await client.SendAsync("GET /other HTTP/1.1\r\nHost: t\r\n\r\n");
        HttpReply other = await client.ReadReplyAsync();
        Assert.Equal("HTTP/1.1 405 Method Not Allowed", other.StatusLine);
        Assert.Equal("PUT", other.Headers["Allow"]);
    }

    [Fact]
    public async Task Head_is_served_by_the_get_route_without_a_body()
    {
        await using var host = new TestHost(app => app.Map(DogMethod.Get, "/dogs", Text("woof")));
        using var client = await RawClient.ConnectAsync(host.Port);

        await client.SendAsync("HEAD /dogs HTTP/1.1\r\nHost: t\r\n\r\n");
        HttpReply head = await client.ReadReplyAsync(expectBody: false);
        Assert.Equal("HTTP/1.1 200 OK", head.StatusLine);
        Assert.Equal("4", head.Headers["Content-Length"]);

        await client.SendAsync("GET /dogs HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("woof", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Routing_uses_the_decoded_path()
    {
        await using var host = new TestHost(app =>
        {
            app.Map(DogMethod.Get, "/hello world", Text("spaced"));
            app.Map(DogMethod.Get, "/dogs/{name}", Text("one segment"));
        });
        using var client = await RawClient.ConnectAsync(host.Port);

        await client.SendAsync("GET /hello%20world HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("spaced", (await client.ReadReplyAsync()).Body);

        await client.SendAsync("GET /dogs/a%2Fb HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 404 ", (await client.ReadReplyAsync()).StatusLine);
    }

    [Fact]
    public async Task Catch_all_route_gets_the_rest_of_the_path()
    {
        await using var host = new TestHost(app =>
            app.Map(DogMethod.Get, "/files/{*path}", static ctx =>
            {
                ctx.Response.Write(ctx.Route["path"u8]);
                return ValueTask.CompletedTask;
            }));
        using var client = await RawClient.ConnectAsync(host.Port);

        await client.SendAsync("GET /files/a/b.txt HTTP/1.1\r\nHost: t\r\n\r\n");

        Assert.Equal("a/b.txt", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Route_groups_prefix_their_routes()
    {
        await using var host = new TestHost(app =>
        {
            DogRouteGroup api = app.MapGroup("/api");
            api.Map(DogMethod.Get, "/ping", Text("pong"));

            DogRouteGroup v1 = api.MapGroup("/v1");
            v1.Map(DogMethod.Get, "/dogs/{id:int}", static ctx =>
            {
                ctx.Response.Write(ctx.Route["id"u8]);
                return ValueTask.CompletedTask;
            });
        });
        using var client = await RawClient.ConnectAsync(host.Port);

        await client.SendAsync("GET /api/ping HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("pong", (await client.ReadReplyAsync()).Body);

        await client.SendAsync("GET /api/v1/dogs/7 HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("7", (await client.ReadReplyAsync()).Body);

        await client.SendAsync("GET /ping HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 404 ", (await client.ReadReplyAsync()).StatusLine);
    }

    [Fact]
    public async Task Context_is_exposed_with_the_matched_pattern()
    {
        await using var host = new TestHost(app =>
            app.Map(DogMethod.Get, "/dogs/{id:int}", static ctx =>
            {
                ctx.Response.Write(ctx.RoutePattern!);
                return ValueTask.CompletedTask;
            }));
        using var client = await RawClient.ConnectAsync(host.Port);

        await client.SendAsync("GET /dogs/3 HTTP/1.1\r\nHost: t\r\n\r\n");

        Assert.Equal("/dogs/{id:int}", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Concurrent_requests_do_not_share_context_state()
    {
        await using var host = new TestHost(app =>
            app.Map(DogMethod.Get, "/echo/{v}", static async ctx =>
            {
                await Task.Yield();
                ctx.Response.Write(ctx.Route["v"u8]);
            }));

        IEnumerable<Task> clients = Enumerable.Range(0, 20).Select(async c =>
        {
            using var client = await RawClient.ConnectAsync(host.Port);

            for (int i = 0; i < 50; i++)
            {
                string value = $"c{c}-r{i}";
                await client.SendAsync($"GET /echo/{value} HTTP/1.1\r\nHost: t\r\n\r\n");

                HttpReply reply = await client.ReadReplyAsync();
                Assert.Equal(value, reply.Body);
            }
        });

        await Task.WhenAll(clients);
    }

    [Fact]
    public async Task Pipelined_requests_get_their_own_route_values()
    {
        await using var host = new TestHost(app =>
            app.Map(DogMethod.Get, "/echo/{v}", static ctx =>
            {
                ctx.Response.Write(ctx.Route["v"u8]);
                return ValueTask.CompletedTask;
            }));
        using var client = await RawClient.ConnectAsync(host.Port);

        await client.SendAsync(
            "GET /echo/one HTTP/1.1\r\nHost: t\r\n\r\n" +
            "GET /echo/two HTTP/1.1\r\nHost: t\r\n\r\n" +
            "GET /echo/three HTTP/1.1\r\nHost: t\r\n\r\n");

        Assert.Equal("one", (await client.ReadReplyAsync()).Body);
        Assert.Equal("two", (await client.ReadReplyAsync()).Body);
        Assert.Equal("three", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Sync_handler_exception_gives_500_and_the_app_keeps_working()
    {
        await using var host = new TestHost(app =>
        {
            app.Map(DogMethod.Get, "/boom", static ctx => throw new InvalidOperationException("boom"));
            app.Map(DogMethod.Get, "/ok", Text("ok"));
        });
        using var client = await RawClient.ConnectAsync(host.Port);

        await client.SendAsync("GET /boom HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("HTTP/1.1 500 Internal Server Error", (await client.ReadReplyAsync()).StatusLine);

        await client.SendAsync("GET /ok HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("ok", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public async Task Async_handler_exception_gives_500_and_the_app_keeps_working()
    {
        await using var host = new TestHost(app =>
        {
            app.Map(DogMethod.Get, "/boom", static async ctx =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            });
            app.Map(DogMethod.Get, "/ok", Text("ok"));
        });
        using var client = await RawClient.ConnectAsync(host.Port);

        await client.SendAsync("GET /boom HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("HTTP/1.1 500 Internal Server Error", (await client.ReadReplyAsync()).StatusLine);

        await client.SendAsync("GET /ok HTTP/1.1\r\nHost: t\r\n\r\n");
        Assert.Equal("ok", (await client.ReadReplyAsync()).Body);
    }

    [Fact]
    public void Routes_cannot_be_added_after_the_handler_is_requested()
    {
        var app = new DogApp();
        app.Map(DogMethod.Get, "/a", RouteKit.Noop);

        _ = app.Handler;

        Assert.Throws<InvalidOperationException>(() => app.Map(DogMethod.Get, "/b", RouteKit.Noop));
    }

    [Fact]
    public void Registration_errors_are_reported_immediately()
    {
        var app = new DogApp();

        Assert.ThrowsAny<ArgumentException>(() => app.Map(DogMethod.Get, "bad", RouteKit.Noop));
        Assert.ThrowsAny<ArgumentException>(() => app.Map(DogMethod.Other, "/a", RouteKit.Noop));
        Assert.ThrowsAny<ArgumentException>(() => app.MapGroup("api"));
    }
}
