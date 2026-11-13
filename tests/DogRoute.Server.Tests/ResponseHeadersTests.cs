using System.Text;

namespace DogRoute.Server.Tests;

public class ResponseHeadersTests
{
    private static string Render(DogResponse res, bool keepAlive = true)
    {
        using var output = new ByteBuffer(256);
        HttpWriter.Write(output, res, keepAlive, isHead: false);
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    [Fact]
    public void Byte_overload_writes_header()
    {
        using var res = new DogResponse();
        res.Headers.Add("X-Dog"u8, "woof"u8);

        Assert.Contains("X-Dog: woof\r\n", Render(res));
        Assert.Equal(1, res.Headers.Count);
    }

    [Fact]
    public void String_overload_writes_header()
    {
        using var res = new DogResponse();
        res.Headers.Add("X-Name", "Rex the dog");

        Assert.Contains("X-Name: Rex the dog\r\n", Render(res));
    }

    [Fact]
    public void Duplicate_headers_are_all_written()
    {
        using var res = new DogResponse();
        res.Headers.Add("Set-Cookie", "a=1");
        res.Headers.Add("Set-Cookie", "b=2");

        string text = Render(res);

        Assert.Contains("Set-Cookie: a=1\r\n", text);
        Assert.Contains("Set-Cookie: b=2\r\n", text);
    }

    [Fact]
    public void Custom_headers_are_before_the_end_of_the_head()
    {
        using var res = new DogResponse();
        res.Headers.Add("X-Dog", "woof");
        res.Write("body"u8);

        string text = Render(res);

        Assert.True(text.IndexOf("X-Dog", StringComparison.Ordinal) < text.IndexOf("\r\n\r\n", StringComparison.Ordinal));
        Assert.EndsWith("\r\n\r\nbody", text);
    }

    [Fact]
    public void Tab_is_allowed_in_values()
    {
        using var res = new DogResponse();
        res.Headers.Add("X-Tab", "a\tb");

        Assert.Contains("X-Tab: a\tb\r\n", Render(res));
    }

    [Theory]
    [InlineData("Content-Length")]
    [InlineData("content-type")]
    [InlineData("CONNECTION")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Date")]
    [InlineData("Server")]
    public void Reserved_headers_are_rejected(string name)
    {
        using var res = new DogResponse();

        Assert.Throws<ArgumentException>(() => res.Headers.Add(name, "x"));
        Assert.Throws<ArgumentException>(() => res.Headers.Add(Encoding.ASCII.GetBytes(name), "x"u8));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bad Name")]
    [InlineData("X:Y")]
    [InlineData("X\r\nY")]
    [InlineData("Ключ")]
    public void Invalid_names_are_rejected(string name)
    {
        using var res = new DogResponse();

        Assert.Throws<ArgumentException>(() => res.Headers.Add(name, "x"));
    }

    [Theory]
    [InlineData("a\r\nInjected: 1")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    [InlineData("привет")]
    public void Invalid_values_are_rejected(string value)
    {
        using var res = new DogResponse();

        Assert.Throws<ArgumentException>(() => res.Headers.Add("X-Test", value));
    }

    [Fact]
    public void Invalid_byte_values_are_rejected()
    {
        using var res = new DogResponse();

        Assert.Throws<ArgumentException>(() => res.Headers.Add("X-Test"u8, "a\r\nb"u8));
    }

    [Fact]
    public void Rejected_header_leaves_nothing_behind()
    {
        using var res = new DogResponse();
        res.Headers.Add("X-Ok", "1");

        Assert.Throws<ArgumentException>(() => res.Headers.Add("X-Bad", "a\r\nb"));

        Assert.Equal(1, res.Headers.Count);
        string text = Render(res);
        Assert.Contains("X-Ok: 1\r\n", text);
        Assert.DoesNotContain("X-Bad", text);
    }

    [Fact]
    public void Redirect_sets_status_and_location()
    {
        using var res = new DogResponse();

        res.Redirect("/new", 301);

        string text = Render(res);
        Assert.StartsWith("HTTP/1.1 301 Moved Permanently\r\n", text);
        Assert.Contains("Location: /new\r\n", text);
    }

    [Fact]
    public void Redirect_byte_overload_defaults_to_302()
    {
        using var res = new DogResponse();

        res.Redirect("/new"u8);

        Assert.Equal(302, res.StatusCode);
        Assert.Contains("Location: /new\r\n", Render(res));
    }

    [Fact]
    public void Redirect_requires_a_3xx_status()
    {
        using var res = new DogResponse();

        Assert.Throws<ArgumentOutOfRangeException>(() => res.Redirect("/x", 200));
        Assert.Equal(200, res.StatusCode);
        Assert.Equal(0, res.Headers.Count);
    }

    [Fact]
    public void Reset_clears_headers()
    {
        using var res = new DogResponse();
        res.Headers.Add("X-Dog", "woof");

        res.Reset();

        Assert.Equal(0, res.Headers.Count);
        Assert.DoesNotContain("X-Dog", Render(res));
    }

    [Fact]
    public void No_content_response_still_has_custom_headers()
    {
        using var res = new DogResponse { StatusCode = 204 };
        res.Headers.Add("X-Dog", "woof");

        string text = Render(res);

        Assert.StartsWith("HTTP/1.1 204 No Content\r\n", text);
        Assert.Contains("X-Dog: woof\r\n", text);
        Assert.DoesNotContain("Content-Length", text);
    }
}
