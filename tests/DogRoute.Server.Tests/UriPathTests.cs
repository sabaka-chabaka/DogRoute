using System.Text;

namespace DogRoute.Server.Tests;

public class UriPathTests
{
    private static string? Normalize(string raw)
    {
        byte[] src = Encoding.ASCII.GetBytes(raw);
        byte[] dst = new byte[src.Length];

        return UriPath.TryNormalize(src, dst, out int length) ? Encoding.UTF8.GetString(dst, 0, length) : null;
    }

    [Theory]
    [InlineData("/hello%20world", "/hello world")]
    [InlineData("/caf%C3%A9", "/caf\u00e9")]
    [InlineData("/a%2Fb", "/a/b")]
    [InlineData("/a%2fb", "/a/b")]
    [InlineData("/a/./b", "/a/b")]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("/a/b/..", "/a/")]
    [InlineData("/a/.", "/a/")]
    [InlineData("/.", "/")]
    [InlineData("/a/..", "/")]
    [InlineData("/a/../b", "/b")]
    [InlineData("/a//../b", "/a/b")]
    [InlineData("/.hidden/x", "/.hidden/x")]
    [InlineData("/a/..b/c", "/a/..b/c")]
    [InlineData("/%2e/a", "/a")]
    [InlineData("/a/%2E%2E/b", "/b")]
    public void Normalizes_valid_paths(string raw, string expected)
    {
        Assert.Equal(expected, Normalize(raw));
    }

    [Theory]
    [InlineData("/%zz")]
    [InlineData("/%4")]
    [InlineData("/%")]
    [InlineData("/a%00b")]
    [InlineData("/%C3%28")]
    [InlineData("/..")]
    [InlineData("/../x")]
    [InlineData("/a/../..")]
    [InlineData("/%2e%2e/x")]
    [InlineData("/a/%2E%2E/%2E%2E/x")]
    public void Rejects_invalid_or_escaping_paths(string raw)
    {
        Assert.Null(Normalize(raw));
    }

    [Theory]
    [InlineData("/plain/path", false)]
    [InlineData("/file.tar.gz", false)]
    [InlineData("/a%20b", true)]
    [InlineData("/a/./b", true)]
    [InlineData("/a/../b", true)]
    [InlineData("/.hidden", true)]
    public void Detects_when_normalization_is_needed(string raw, bool expected)
    {
        Assert.Equal(expected, UriPath.NeedsNormalization(Encoding.ASCII.GetBytes(raw)));
    }
}
