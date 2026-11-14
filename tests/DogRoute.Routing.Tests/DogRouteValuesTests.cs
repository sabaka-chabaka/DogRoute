using System.Text;
using DogRoute.Server;

namespace DogRoute.Routing.Tests;

public class DogRouteValuesTests
{
    private static DogRouteValues Values(string pattern, string path)
    {
        RouteTree tree = RouteKit.GetTree(pattern);
        RouteEndpoint? endpoint = RouteKit.Match(tree, DogMethod.Get, path, out DogRouteValues values, out _);

        Assert.NotNull(endpoint);
        return values;
    }

    [Fact]
    public void Int_values_are_parsed()
    {
        DogRouteValues values = Values("/dogs/{id:int}", "/dogs/42");

        Assert.True(values.TryGetInt32("id"u8, out int id));
        Assert.Equal(42, id);

        Assert.True(values.TryGetInt64("id"u8, out long wide));
        Assert.Equal(42L, wide);
    }

    [Fact]
    public void Long_values_are_parsed()
    {
        DogRouteValues values = Values("/dogs/{id:long}", "/dogs/99999999999");

        Assert.True(values.TryGetInt64("id"u8, out long id));
        Assert.Equal(99999999999L, id);
        Assert.False(values.TryGetInt32("id"u8, out _));
    }

    [Fact]
    public void Guid_values_are_parsed()
    {
        var expected = Guid.NewGuid();
        DogRouteValues values = Values("/x/{id:guid}", $"/x/{expected}");

        Assert.True(values.TryGetGuid("id"u8, out Guid id));
        Assert.Equal(expected, id);
    }

    [Fact]
    public void Values_that_are_not_numbers_fail_to_parse()
    {
        DogRouteValues values = Values("/dogs/{id}", "/dogs/12x");

        Assert.False(values.TryGetInt32("id"u8, out _));
        Assert.False(values.TryGetInt64("id"u8, out _));
        Assert.False(values.TryGetGuid("id"u8, out _));
    }

    [Fact]
    public void GetString_decodes_utf8()
    {
        DogRouteValues values = Values("/hello/{name}", "/hello/caf\u00e9");

        Assert.Equal("caf\u00e9", values.GetString("name"u8));
    }

    [Fact]
    public void Missing_names_are_reported()
    {
        DogRouteValues values = Values("/dogs/{id}", "/dogs/1");

        Assert.False(values.TryGet("nope"u8, out _));
        Assert.True(values["nope"u8].IsEmpty);
        Assert.Null(values.GetString("nope"u8));
    }

    [Fact]
    public void Names_are_case_insensitive()
    {
        DogRouteValues values = Values("/dogs/{Id}", "/dogs/7");

        Assert.Equal("7", Encoding.UTF8.GetString(values["ID"u8]));
        Assert.Equal("7", Encoding.UTF8.GetString(values["id"u8]));
    }

    [Fact]
    public void Indexer_returns_the_value()
    {
        DogRouteValues values = Values("/dogs/{id}", "/dogs/abc");

        Assert.Equal("abc", Encoding.UTF8.GetString(values["id"u8]));
    }

    [Fact]
    public void Count_and_GetAt_enumerate_the_parameters()
    {
        DogRouteValues values = Values("/a/{x}/b/{y}", "/a/1/b/2");

        Assert.Equal(2, values.Count);

        values.GetAt(0, out ReadOnlySpan<byte> name0, out ReadOnlySpan<byte> value0);
        Assert.Equal("x", Encoding.UTF8.GetString(name0));
        Assert.Equal("1", Encoding.UTF8.GetString(value0));

        values.GetAt(1, out ReadOnlySpan<byte> name1, out ReadOnlySpan<byte> value1);
        Assert.Equal("y", Encoding.UTF8.GetString(name1));
        Assert.Equal("2", Encoding.UTF8.GetString(value1));

        Assert.Throws<ArgumentOutOfRangeException>(() => values.GetAt(2, out _, out _));
    }

    [Fact]
    public void Route_without_params_has_none()
    {
        DogRouteValues values = Values("/plain", "/plain");

        Assert.Equal(0, values.Count);
    }

    [Fact]
    public void Values_are_empty_after_a_failed_match()
    {
        RouteTree tree = RouteKit.GetTree("/dogs/{id}");

        RouteKit.Match(tree, DogMethod.Get, "/dogs/1", out DogRouteValues values, out _);
        values.Path = Encoding.UTF8.GetBytes("/cats");
        tree.Match(DogMethod.Get, values, out _);

        Assert.Equal(0, values.Count);
        Assert.False(values.TryGet("id"u8, out _));
    }
}
