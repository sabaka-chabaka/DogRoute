namespace DogRoute.Routing.Tests;

public class RouteGroupTests
{
    [Theory]
    [InlineData("/api", "/api")]
    [InlineData("/api/", "/api")]
    [InlineData("/", "")]
    [InlineData("/a/b", "/a/b")]
    [InlineData("/a/{id}", "/a/{id}")]
    public void Prefix_is_normalized(string prefix, string expected)
    {
        Assert.Equal(expected, DogRouteGroup.NormalizePrefix(prefix));
    }

    [Theory]
    [InlineData("")]
    [InlineData("api")]
    public void Invalid_prefixes_are_rejected(string prefix)
    {
        Assert.ThrowsAny<ArgumentException>(() => DogRouteGroup.NormalizePrefix(prefix));
    }

    [Theory]
    [InlineData("", "/dogs", "/dogs")]
    [InlineData("", "/", "/")]
    [InlineData("/api", "/dogs", "/api/dogs")]
    [InlineData("/api", "/", "/api")]
    [InlineData("/api/v1", "/dogs/{id}", "/api/v1/dogs/{id}")]
    public void Prefix_and_pattern_are_combined(string prefix, string pattern, string expected)
    {
        Assert.Equal(expected, DogRouteGroup.Combine(prefix, pattern));
    }

    [Theory]
    [InlineData("")]
    [InlineData("dogs")]
    public void Patterns_must_start_with_a_slash(string pattern)
    {
        Assert.ThrowsAny<ArgumentException>(() => DogRouteGroup.Combine("/api", pattern));
    }
}
