namespace DogRoute.Routing.Tests;

public class RoutePatternTests
{
    [Fact]
    public void Root_has_no_segments()
    {
        Assert.Empty(RoutePattern.Parse("/"));
    }

    [Fact]
    public void Parses_literals_params_and_catch_all()
    {
        List<PatternSegment> segments = RoutePattern.Parse("/dogs/{id:int}/{*rest}");

        Assert.Equal(3, segments.Count);

        Assert.Equal(SegmentKind.Literal, segments[0].Kind);
        Assert.Equal("dogs", segments[0].Text);

        Assert.Equal(SegmentKind.Param, segments[1].Kind);
        Assert.Equal("id", segments[1].Text);
        Assert.Equal(ConstraintKind.Int, segments[1].Constraint);

        Assert.Equal(SegmentKind.CatchAll, segments[2].Kind);
        Assert.Equal("rest", segments[2].Text);
    }

    [Theory]
    [InlineData("/{id}", ConstraintKind.None)]
    [InlineData("/{id:int}", ConstraintKind.Int)]
    [InlineData("/{id:INT}", ConstraintKind.Int)]
    [InlineData("/{id:long}", ConstraintKind.Long)]
    [InlineData("/{id:guid}", ConstraintKind.Guid)]
    public void Parses_constraints(string pattern, ConstraintKind expected)
    {
        Assert.Equal(expected, RoutePattern.Parse(pattern)[0].Constraint);
    }

    [Theory]
    [InlineData("/a")]
    [InlineData("/a/")]
    [InlineData("/a/b/c")]
    [InlineData("/a-b/{x_y}")]
    [InlineData("/hello world")]
    [InlineData("/files/{*path}")]
    public void Valid_patterns_are_accepted(string pattern)
    {
        Assert.NotEmpty(RoutePattern.Parse(pattern));
    }

    [Fact]
    public void Trailing_slash_is_ignored()
    {
        Assert.Single(RoutePattern.Parse("/a/"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("dogs")]
    [InlineData("//")]
    [InlineData("/a//b")]
    [InlineData("/{}")]
    [InlineData("/{*}")]
    [InlineData("/{id")]
    [InlineData("/id}")]
    [InlineData("/a{id}")]
    [InlineData("/{id}b")]
    [InlineData("/{*rest}/x")]
    [InlineData("/{id:foo}")]
    [InlineData("/{id:}")]
    [InlineData("/{a}/{a}")]
    [InlineData("/{a}/{A:int}")]
    [InlineData("/{bad name}")]
    [InlineData("/{*a:int}")]
    [InlineData("/.")]
    [InlineData("/a/../b")]
    public void Invalid_patterns_are_rejected(string pattern)
    {
        Assert.ThrowsAny<ArgumentException>(() => RoutePattern.Parse(pattern));
    }

    [Fact]
    public void Null_pattern_is_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => RoutePattern.Parse(null!));
    }
}
