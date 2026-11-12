using System.Net.Sockets;
using System.Text;

namespace DogRoute.Server.Tests.Support;

internal sealed record HttpReply(string StatusLine, Dictionary<string, string> Headers, string Body);

internal sealed class RawClient : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly TcpClient _tcp = new() { NoDelay = true };
    private NetworkStream _stream = null!;

    public static async Task<RawClient> ConnectAsync(int port)
    {
        var client = new RawClient();
        using var cts = new CancellationTokenSource(Timeout);
        await client._tcp.ConnectAsync("127.0.0.1", port, cts.Token);
        client._stream = client._tcp.GetStream();
        return client;
    }

    public async Task SendAsync(string text)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(text), cts.Token);
    }

    public async Task<HttpReply> ReadReplyAsync(bool expectBody = true)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var head = new StringBuilder();
        var one = new byte[1];

        while (!(head.Length >= 4 && head.ToString(head.Length - 4, 4) == "\r\n\r\n"))
        {
            int n = await _stream.ReadAsync(one, cts.Token);
            if (n == 0) throw new EndOfStreamException("connection closed before full response head");
            head.Append((char)one[0]);
        }

        string[] lines = head.ToString().TrimEnd('\r', '\n').Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            string[] kv = lines[i].Split(':', 2);
            headers[kv[0].Trim()] = kv[1].Trim();
        }

        string body = "";
        if (expectBody && headers.TryGetValue("Content-Length", out string? cl))
        {
            var buf = new byte[int.Parse(cl)];
            await _stream.ReadExactlyAsync(buf, cts.Token);
            body = Encoding.UTF8.GetString(buf);
        }

        return new HttpReply(lines[0], headers, body);
    }

    public async Task<bool> WaitForCloseAsync()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var one = new byte[1];
        try
        {
            return await _stream.ReadAsync(one, cts.Token) == 0;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public void Dispose() => _tcp.Dispose();
}
