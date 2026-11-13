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

    private static readonly PipeOptions PipeOpts = new(
        pool: MemoryPool<byte>.Shared,
        readerScheduler: PipeScheduler.Inline,
        writerScheduler: PipeScheduler.Inline,
        pauseWriterThreshold: HttpParser.MaxHeadSize + HttpParser.MaxBodySize + 4096,
        resumeWriterThreshold: 64 * 1024,
        minimumSegmentSize: 4096,
        useSynchronizationContext: false);

    private long _deadline;
    private int _state = StateIdle;

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
            socket.Dispose();
            try { await pipe.Reader.CompleteAsync(); } catch { }
            try { await filling; } catch { }

            request.Dispose();
            response.Dispose();
            output.Dispose();
        }
    }

    internal void CheckTimeout(long now)
    {
        long deadline = Volatile.Read(ref _deadline);
        if (deadline != 0 && now >= deadline && Volatile.Read(ref _state) != StateHandling)
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

    private async Task FillPipeAsync(PipeWriter writer)
    {
        try
        {
            while (true)
            {
                Memory<byte> memory = writer.GetMemory(4096);
                int read = await socket.ReceiveAsync(memory, SocketFlags.None);
                if (read == 0) break;

                writer.Advance(read);
                FlushResult flush = await writer.FlushAsync();
                if (flush.IsCompleted) break;
            }
            await writer.CompleteAsync();
        }
        catch (Exception ex)
        {
            await writer.CompleteAsync(ex);
        }
    }

    private async Task ProcessAsync(PipeReader reader, DogRequest request, DogResponse response, ByteBuffer output)
    {
        EnterIdle(options.RequestTimeout);
        bool idle = true;

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

                    EnterHandling();
                    handled = true;

                    if (status == ParseStatus.Bad)
                    {
                        response.Reset();
                        response.StatusCode = 400;
                        response.Write("Bad Request"u8);
                        HttpWriter.Write(output, response, keepAlive: false, isHead: false);

                        consumed = examined;
                        close = true;
                        break;
                    }

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

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask SendAllAsync(ByteBuffer output)
    {
        ReadOnlyMemory<byte> data = output.WrittenMemory;
        while (!data.IsEmpty)
        {
            int sent = await socket.SendAsync(data, SocketFlags.None);
            data = data[sent..];
        }
    }
}
