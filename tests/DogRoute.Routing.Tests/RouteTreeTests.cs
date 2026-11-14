using System.Text;
using DogRoute.Server;

namespace DogRoute.Routing.Tests;

public class RouteTreeTests
{
    private static string? PatternOf(RouteTree tree, string path, DogMethod method = DogMethod.Get) =>
        RouteKit.Match(tree, method, path, out _, out _)?.Pattern;

    [Fact]
    public void Root_route_matches_only_the_root()
    {
        RouteTree tree = RouteKit.GetTree("/");

        Assert.Equal("/", PatternOf(tree, "/"));
        Assert.Null(PatternOf(tree, "/x"));
    }

    [Theory]
    [InlineData("/dogs", "/dogs")]
    [InlineData("/dogs/", "/dogs")]
    [InlineData("/cats", null)]
    [InlineData("/dogs/x", null)]
    [InlineData("/Dogs", null)]
    public void Literal_matching(string path, string? expected)
    {
        RouteTree tree = RouteKit.GetTree("/dogs");

        Assert.Equal(expected, PatternOf(tree, path));
    }

    [Fact]
    public void Trailing_slash_in_pattern_is_ignored()
    {
        RouteTree tree = RouteKit.GetTree("/dogs/");

        Assert.Equal("/dogs/", PatternOf(tree, "/dogs"));
        Assert.Equal("/dogs/", PatternOf(tree, "/dogs/"));
    }

    [Fact]
    public void Param_value_is_extracted()
    {
        RouteTree tree = RouteKit.GetTree("/dogs/{id}");

        RouteEndpoint? endpoint = RouteKit.Match(tree, DogMethod.Get, "/dogs/42", out DogRouteValues values, out _);

        Assert.NotNull(endpoint);
        Assert.True(values.TryGet("id"u8, out ReadOnlySpan<byte> id));
        Assert.Equal("42", Encoding.UTF8.GetString(id));
    }

    [Fact]
    public void Param_does_not_match_an_empty_segment()
    {
        RouteTree tree = RouteKit.GetTree("/a/{x}/b");

        Assert.Null(PatternOf(tree, "/a//b"));
        Assert.Equal("/a/{x}/b", PatternOf(tree, "/a/1/b"));
    }

    [Fact]
    public void Literal_beats_param()
    {
        RouteTree tree = RouteKit.GetTree("/dogs/{id}", "/dogs/new");

        Assert.Equal("/dogs/new", PatternOf(tree, "/dogs/new"));
        Assert.Equal("/dogs/{id}", PatternOf(tree, "/dogs/5"));
    }

    [Fact]
    public void Matching_backtracks_from_a_dead_end_literal()
    {
        RouteTree tree = RouteKit.GetTree("/a/b/c", "/a/{x}/d");

        Assert.Equal("/a/b/c", PatternOf(tree, "/a/b/c"));
        Assert.Equal("/a/{x}/d", PatternOf(tree, "/a/b/d"));
    }

    [Theory]
    [InlineData("/x/5", "/x/{id:int}")]
    [InlineData("/x/-7", "/x/{id:int}")]
    [InlineData("/x/rex", "/x/{name}")]
    [InlineData("/x/99999999999", "/x/{name}")] // не влезает в int
    [InlineData("/x/5a", "/x/{name}")]
    public void Constrained_param_beats_unconstrained(string path, string expected)
    {
        RouteTree tree = RouteKit.GetTree("/x/{name}", "/x/{id:int}");

        Assert.Equal(expected, PatternOf(tree, path));
    }

    [Fact]
    public void Int_is_tried_before_long()
    {
        RouteTree tree = RouteKit.GetTree("/x/{id:long}", "/x/{id:int}");

        Assert.Equal("/x/{id:int}", PatternOf(tree, "/x/5"));
        Assert.Equal("/x/{id:long}", PatternOf(tree, "/x/99999999999"));
    }

    [Fact]
    public void Guid_constraint()
    {
        RouteTree tree = RouteKit.GetTree("/x/{id:guid}");

        Assert.Equal("/x/{id:guid}", PatternOf(tree, "/x/00000000-0000-0000-0000-000000000000"));
        Assert.Null(PatternOf(tree, "/x/nope"));
    }

    [Theory]
    [InlineData("/files/a/b/c", "a/b/c")]
    [InlineData("/files/a", "a")]
    [InlineData("/files/a/", "a")]
    [InlineData("/files", "")]
    [InlineData("/files/", "")]
    [InlineData("/files//b", "/b")]
    public void Catch_all_takes_the_rest(string path, string expected)
    {
        RouteTree tree = RouteKit.GetTree("/files/{*path}");

        RouteEndpoint? endpoint = RouteKit.Match(tree, DogMethod.Get, path, out DogRouteValues values, out _);

        Assert.NotNull(endpoint);
        Assert.True(values.TryGet("path"u8, out ReadOnlySpan<byte> rest));
        Assert.Equal(expected, Encoding.UTF8.GetString(rest));
    }

    [Fact]
    public void Literal_beats_catch_all()
    {
        RouteTree tree = RouteKit.GetTree("/files/{*path}", "/files/special");

        Assert.Equal("/files/special", PatternOf(tree, "/files/special"));
        Assert.Equal("/files/{*path}", PatternOf(tree, "/files/other"));
    }

    [Fact]
    public void Catch_all_is_the_fallback_when_a_param_route_dead_ends()
    {
        RouteTree tree = RouteKit.GetTree("/a/{x}/b", "/a/{*rest}");

        Assert.Equal("/a/{x}/b", PatternOf(tree, "/a/1/b"));
        Assert.Equal("/a/{*rest}", PatternOf(tree, "/a/1/c"));
    }

    [Fact]
    public void Own_route_beats_catch_all_for_the_same_path()
    {
        RouteTree tree = RouteKit.GetTree("/files", "/files/{*path}");

        Assert.Equal("/files", PatternOf(tree, "/files"));
    }

    [Fact]
    public void Root_catch_all_matches_everything()
    {
        RouteTree tree = RouteKit.GetTree("/{*all}");

        Assert.Equal("/{*all}", PatternOf(tree, "/"));
        Assert.Equal("/{*all}", PatternOf(tree, "/a/b/c"));
    }

    [Fact]
    public void Several_params_are_extracted_in_order()
    {
        RouteTree tree = RouteKit.GetTree("/a/{x}/b/{y}");

        RouteKit.Match(tree, DogMethod.Get, "/a/1/b/2", out DogRouteValues values, out _);

        Assert.True(values.TryGet("x"u8, out ReadOnlySpan<byte> x));
        Assert.True(values.TryGet("y"u8, out ReadOnlySpan<byte> y));
        Assert.Equal("1", Encoding.UTF8.GetString(x));
        Assert.Equal("2", Encoding.UTF8.GetString(y));
    }

    [Fact]
    public void Same_path_with_different_methods()
    {
        RouteTree tree = RouteKit.Tree((DogMethod.Get, "/dogs"), (DogMethod.Post, "/dogs"));

        Assert.NotNull(PatternOf(tree, "/dogs", DogMethod.Get));
        Assert.NotNull(PatternOf(tree, "/dogs", DogMethod.Post));
    }

    [Fact]
    public void Unsupported_method_reports_the_allowed_ones()
    {
        RouteTree tree = RouteKit.Tree((DogMethod.Get, "/dogs"), (DogMethod.Post, "/dogs"));

        RouteEndpoint? endpoint = RouteKit.Match(tree, DogMethod.Delete, "/dogs", out _, out int allowed);

        Assert.Null(endpoint);
        Assert.Equal((1 << (int)DogMethod.Get) | (1 << (int)DogMethod.Post), allowed);
    }

    [Fact]
    public void Unknown_path_reports_nothing_allowed()
    {
        RouteTree tree = RouteKit.Tree((DogMethod.Get, "/dogs"));

        RouteEndpoint? endpoint = RouteKit.Match(tree, DogMethod.Get, "/cats", out _, out int allowed);

        Assert.Null(endpoint);
        Assert.Equal(0, allowed);
    }

    [Fact]
    public void A_later_route_with_the_right_method_wins_over_a_405()
    {
        // /a/b/c есть, но только для POST; /a/{x}/c умеет GET
        RouteTree tree = RouteKit.Tree((DogMethod.Post, "/a/b/c"), (DogMethod.Get, "/a/{x}/c"));

        Assert.Equal("/a/{x}/c", PatternOf(tree, "/a/b/c", DogMethod.Get));
    }

    [Fact]
    public void Head_falls_back_to_get()
    {
        var tree = new RouteTree();
        DogRouteHandler get = static _ => ValueTask.CompletedTask;
        tree.Add(DogMethod.Get, "/dogs", get);

        RouteEndpoint? endpoint = RouteKit.Match(tree, DogMethod.Head, "/dogs", out _, out _);

        Assert.NotNull(endpoint);
        Assert.Same(get, endpoint.Handler);
    }

    [Fact]
    public void Explicit_head_wins_over_get()
    {
        var tree = new RouteTree();
        DogRouteHandler get = static _ => ValueTask.CompletedTask;
        DogRouteHandler head = static _ => default;
        tree.Add(DogMethod.Get, "/dogs", get);
        tree.Add(DogMethod.Head, "/dogs", head);

        RouteEndpoint? endpoint = RouteKit.Match(tree, DogMethod.Head, "/dogs", out _, out _);

        Assert.NotNull(endpoint);
        Assert.Same(head, endpoint.Handler);
    }

    [Fact]
    public void Other_method_never_matches()
    {
        RouteTree tree = RouteKit.GetTree("/dogs");

        RouteEndpoint? endpoint = RouteKit.Match(tree, DogMethod.Other, "/dogs", out _, out int allowed);

        Assert.Null(endpoint);
        Assert.NotEqual(0, allowed);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("dogs")]
    public void Paths_that_do_not_start_with_a_slash_do_not_match(string path)
    {
        RouteTree tree = RouteKit.GetTree("/{*all}");

        Assert.Null(PatternOf(tree, path));
    }

    [Fact]
    public void Paths_with_too_many_segments_do_not_match()
    {
        RouteTree tree = RouteKit.GetTree("/{*rest}");
        string atLimit = "/" + string.Join('/', Enumerable.Repeat("a", RouteTree.MaxSegments));
        string overLimit = atLimit + "/a";

        Assert.NotNull(PatternOf(tree, atLimit));
        Assert.Null(PatternOf(tree, overLimit));
    }

    [Fact]
    public void Patterns_with_too_many_segments_are_rejected()
    {
        var tree = new RouteTree();
        string pattern = "/" + string.Join('/', Enumerable.Repeat("a", RouteTree.MaxSegments + 1));

        Assert.ThrowsAny<ArgumentException>(() => tree.Add(DogMethod.Get, pattern, RouteKit.Noop));
    }

    [Fact]
    public void Duplicate_routes_are_rejected()
    {
        var tree = new RouteTree();
        tree.Add(DogMethod.Get, "/a/{id}", RouteKit.Noop);

        Assert.Throws<InvalidOperationException>(() => tree.Add(DogMethod.Get, "/a/{name}", RouteKit.Noop));
        Assert.Throws<InvalidOperationException>(() => tree.Add(DogMethod.Get, "/a/{id}", RouteKit.Noop));
    }

    [Fact]
    public void Same_path_with_another_method_or_constraint_is_not_a_duplicate()
    {
        var tree = new RouteTree();
        tree.Add(DogMethod.Get, "/a/{id}", RouteKit.Noop);
        tree.Add(DogMethod.Post, "/a/{id}", RouteKit.Noop);
        tree.Add(DogMethod.Get, "/a/{id:int}", RouteKit.Noop);

        Assert.Equal("/a/{id:int}", PatternOf(tree, "/a/5"));
    }

    [Fact]
    public void Other_method_cannot_be_registered()
    {
        var tree = new RouteTree();

        Assert.ThrowsAny<ArgumentException>(() => tree.Add(DogMethod.Other, "/a", RouteKit.Noop));
    }

    [Fact]
    public void Null_handler_is_rejected()
    {
        var tree = new RouteTree();

        Assert.ThrowsAny<ArgumentException>(() => tree.Add(DogMethod.Get, "/a", null!));
    }
}
