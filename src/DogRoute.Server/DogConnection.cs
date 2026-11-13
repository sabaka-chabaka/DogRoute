using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace DogRoute.Server;

internal sealed class DogConnection(
    Socket socket, DogHandler handler, DogServerOptions options, ConnectionManager manager)
{
    private const int StateIdle = 0;
    private const int StateReading = 1;
    private const int StateHandling = 2;

    private static readonly TimeSpan LingerTimeout = TimeSpan.FromMilliseconds(500);

    private static readonly byte[] TimeoutResponse =
        "HTTP/1.1 408 Request Timeout\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray();

    private static readonly PipeOptions PipeOpts = new(
        pool: MemoryPool<byte>.Shared,
        readerScheduler: PipeScheduler.Inline,
        writerScheduler: PipeScheduler.Inline,
        pauseWriterThreshold: HttpParser.MaxRequestWireSize + 64 * 1024,
        resumeWriterThreshold: 64 * 1024,
        minimumSegmentSize: 4096,
        useSynchronizationContext: false);

    private long _deadline;
    private long _sendDeadline;
    private int _state = StateIdle;
    private int _timedOut;

    public async Task RunAsync()
    {
        await Task.Yield();

        var pipe = new Pipe(PipeOpts);
        var request = new DogRequest();
        var response = new DogResponse();
        var output = new ByteBuffer(4096);

        Task filling = FillPipeAsync(pipe.Writer);

        try
        {
            await ProcessAsync(pipe.Reader, request, response, output);
        }
        catch { /*  */ }
        finally
        {
            manager.Remove(this);

            try { socket.Shutdown(SocketShutdown.Send); } catch { }
            try { await pipe.Reader.CompleteAsync(); } catch { }
            try { await filling.WaitAsync(LingerTimeout); } catch { }

            socket.Dispose();
            try { await filling; } catch { }

            request.Dispose();
            response.Dispose();
            output.Dispose();
        }
    }

    internal void CheckTimeout(long now)
    {
        long sendDeadline = Volatile.Read(ref _sendDeadline);
        if (sendDeadline != 0 && now >= sendDeadline)
        {
            Abort();
            return;
        }

        long deadline = Volatile.Read(ref _deadline);
        if (deadline == 0 || now < deadline) return;

        int state = Volatile.Read(ref _state);
        if (state == StateHandling) return;

        if (state == StateReading)
        {
            if (Interlocked.Exchange(ref _timedOut, 1) == 0)
                _ = SendTimeoutAndAbortAsync();
            return;
        }

        Abort();
    }

    internal void CloseIfIdle()
    {
        if (Volatile.Read(ref _state) == StateIdle)
            Abort();
    }

    internal void Abort()
    {
        try { socket.Dispose(); } catch { }
    }

    private async Task SendTimeoutAndAbortAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await socket.SendAsync(TimeoutResponse.AsMemory(), SocketFlags.None, cts.Token);

            socket.Shutdown(SocketShutdown.Send);
            await Task.Delay(LingerTimeout);
        }
        catch { }

        Abort();
    }

    private void EnterIdle(TimeSpan timeout)
    {
        SetDeadline(timeout);
        Volatile.Write(ref _state, StateIdle);
    }

    private void EnterReading()
    {
        SetDeadline(options.RequestTimeout);
        Volatile.Write(ref _state, StateReading);
    }

    private void EnterHandling()
    {
        Volatile.Write(ref _state, StateHandling);
        Volatile.Write(ref _deadline, 0);
    }

    private void SetDeadline(TimeSpan timeout)
    {
        long deadline = timeout == Timeout.InfiniteTimeSpan
            ? 0
            : Environment.TickCount64 + Math.Max(1, (long)timeout.TotalMilliseconds);
        Volatile.Write(ref _deadline, deadline);
    }

    private void ArmSendDeadline()
    {
        if (options.SendTimeout == Timeout.InfiniteTimeSpan) return;

        Volatile.Write(ref _sendDeadline,
            Environment.TickCount64 + Math.Max(1, (long)options.SendTimeout.TotalMilliseconds));
    }

    private async Task FillPipeAsync(PipeWriter writer)
    {
        byte[]? discard = null;
        bool writerCompleted = false;
        Exception? error = null;

        try
        {
            while (true)
            {
                if (writerCompleted)
                {
                    discard ??= ArrayPool<byte>.Shared.Rent(4096);
                    int drained = await socket.ReceiveAsync(discard.AsMemory(), SocketFlags.None);
                    if (drained == 0) break;
                    continue;
                }

                Memory<byte> memory = writer.GetMemory(4096);
                int read = await socket.ReceiveAsync(memory, SocketFlags.None);
                if (read == 0) break;

                writer.Advance(read);
                FlushResult flush = await writer.FlushAsync();
                if (flush.IsCompleted)
                {
                    await writer.CompleteAsync();
                    writerCompleted = true;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            if (discard is not null) ArrayPool<byte>.Shared.Return(discard);
            if (!writerCompleted) await writer.CompleteAsync(error);
        }
    }

    private async Task ProcessAsync(PipeReader reader, DogRequest request, DogResponse response, ByteBuffer output)
    {
        EnterIdle(options.RequestTimeout);
        bool idle = true;
        bool continueSent = false;

        while (true)
        {
            ReadResult result = await reader.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;
            SequencePosition consumed = buffer.Start;
            SequencePosition examined = buffer.End;
            bool close = false;
            bool handled = false;

            if (idle && !buffer.IsEmpty)
            {
                EnterReading();
                idle = false;
            }

            try
            {
                while (!buffer.IsEmpty)
                {
                    ParseStatus status = HttpParser.TryParse(buffer, request, out SequencePosition end);

                    if (status == ParseStatus.NeedMore) break;

                    if (status == ParseStatus.NeedMoreBody)
                    {
                        if (request.ExpectContinue && !continueSent)
                        {
                            output.Write("HTTP/1.1 100 Continue\r\n\r\n"u8);
                            continueSent = true;
                        }
                        break;
                    }

                    EnterHandling();
                    handled = true;

                    if (status != ParseStatus.Ok)
                    {
                        FillParseError(response, status);
                        HttpWriter.Write(output, response, keepAlive: false, isHead: false);

                        consumed = examined;
                        close = true;
                        break;
                    }

                    continueSent = false;
                    response.Reset();
                    try
                    {
                        await handler(request, response);
                    }
                    catch
                    {
                        response.Reset();
                        response.StatusCode = 500;
                        response.Write("Internal Server Error"u8);
                    }

                    bool keepAlive = request.KeepAlive && !manager.IsDraining;
                    HttpWriter.Write(output, response, keepAlive, isHead: request.Method == DogMethod.Head);

                    consumed = end;
                    buffer = buffer.Slice(end);

                    if (!keepAlive)
                    {
                        close = true;
                        break;
                    }
                }

                if (output.Count > 0)
                {
                    await SendAllAsync(output);
                    output.Clear();
                }
            }
            finally
            {
                reader.AdvanceTo(consumed, examined);
            }

            if (close || result.IsCompleted) return;

            if (handled)
            {
                if (buffer.IsEmpty)
                {
                    EnterIdle(options.KeepAliveTimeout);
                    idle = true;
                }
                else
                {
                    EnterReading();
                }
            }
        }
    }

    private static void FillParseError(DogResponse response, ParseStatus status)
    {
        response.Reset();

        switch (status)
        {
            case ParseStatus.PayloadTooLarge:
                response.StatusCode = 413;
                response.Write("Payload Too Large"u8);
                break;
            case ParseStatus.HeadersTooLarge:
                response.StatusCode = 431;
                response.Write("Request Header Fields Too Large"u8);
                break;
            case ParseStatus.ExpectationFailed:
                response.StatusCode = 417;
                response.Write("Expectation Failed"u8);
                break;
            case ParseStatus.NotImplemented:
                response.StatusCode = 501;
                response.Write("Not Implemented"u8);
                break;
            case ParseStatus.VersionNotSupported:
                response.StatusCode = 505;
                response.Write("HTTP Version Not Supported"u8);
                break;
            default:
                response.StatusCode = 400;
                response.Write("Bad Request"u8);
                break;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask SendAllAsync(ByteBuffer output)
    {
        ReadOnlyMemory<byte> data = output.WrittenMemory;

        try
        {
            while (!data.IsEmpty)
            {
                ArmSendDeadline();
                int sent = await socket.SendAsync(data, SocketFlags.None);
                data = data[sent..];
            }
        }
        finally
        {
            Volatile.Write(ref _sendDeadline, 0);
        }
    }
}
