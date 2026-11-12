using System.Text;

namespace DogRoute.Server.Tests;

public class HttpWriterTests
{
    private static string Render(DogResponse res, bool keepAlive = true, bool isHead = false)
    {
        using var output = new ByteBuffer(256);
        HttpWriter.Write(output, res, keepAlive, isHead);
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    [Fact]
    public void Writes_full_200_response()
    {
        using var res = new DogResponse();
        res.Write("Hello, World!"u8);

        string text = Render(res);

        Assert.StartsWith("HTTP/1.1 200 OK\r\n", text);
        Assert.Contains("Server: DogRoute\r\n", text);
        Assert.Contains("Content-Type: text/plain; charset=utf-8\r\n", text);
        Assert.Contains("Content-Length: 13\r\n", text);
        Assert.Contains("Connection: keep-alive\r\n\r\n", text);
        Assert.EndsWith("\r\n\r\nHello, World!", text);
    }

    [Fact]
    public void Writes_valid_date_header()
    {
        using var res = new DogResponse();

        Assert.Matches(@"Date: \w{3}, \d{2} \w{3} \d{4} \d{2}:\d{2}:\d{2} GMT\r\n", Render(res));
    }

    [Fact]
    public void Connection_close_when_keep_alive_is_off()
    {
        using var res = new DogResponse();

        Assert.Contains("Connection: close\r\n\r\n", Render(res, keepAlive: false));
    }

    [Fact]
    public void Head_response_has_length_but_no_body()
    {
        using var res = new DogResponse();
        res.Write("hello"u8);

        string text = Render(res, isHead: true);

        Assert.Contains("Content-Length: 5\r\n", text);
        Assert.EndsWith("\r\n\r\n", text);
    }

    [Fact]
    public void No_content_has_no_body_headers()
    {
        using var res = new DogResponse { StatusCode = 204 };

        string text = Render(res);

        Assert.StartsWith("HTTP/1.1 204 No Content\r\n", text);
        Assert.DoesNotContain("Content-Length", text);
        Assert.DoesNotContain("Content-Type", text);
    }

    [Fact]
    public void Unknown_status_code_still_produces_a_valid_status_line()
    {
        using var res = new DogResponse { StatusCode = 418 };

        Assert.StartsWith("HTTP/1.1 418 Unknown\r\n", Render(res));
    }

    [Fact]
    public void Custom_content_type_is_written()
    {
        using var res = new DogResponse { ContentType = ContentTypes.Json };
        res.Write("{}"u8);

        Assert.Contains("Content-Type: application/json\r\n", Render(res));
    }

    [Fact]
    public void Text_overload_writes_utf8()
    {
        using var res = new DogResponse();
        res.Write("привет");

        Assert.Equal(12, res.BodyLength);
    }

    [Fact]
    public void Reset_restores_defaults()
    {
        using var res = new DogResponse { StatusCode = 500, ContentType = ContentTypes.Json };
        res.Write("x"u8);

        res.Reset();

        Assert.Equal(200, res.StatusCode);
        Assert.Same(ContentTypes.Text, res.ContentType);
        Assert.Equal(0, res.BodyLength);
    }
}
