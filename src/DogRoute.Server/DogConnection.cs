using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace DogRoute.Server;

internal sealed class DogConnection(Socket socket, DogHandler handler)
{
    private static readonly PipeOptions PipeOpts = new(
        pool: MemoryPool<byte>.Shared,
        readerScheduler: PipeScheduler.Inline,
        writerScheduler: PipeScheduler.Inline,
        pauseWriterThreshold: HttpParser.MaxHeadSize + HttpParser.MaxBodySize + 4096,
        resumeWriterThreshold: 64 * 1024,
        minimumSegmentSize: 4096,
        useSynchronizationContext: false);

    public async Task RunAsync(CancellationToken serverCt)
    {
        await Task.Yield();

        var pipe = new Pipe(PipeOpts);
        var request = new DogRequest();
        var response = new DogResponse();
        var output = new ByteBuffer(4096);

        using CancellationTokenRegistration reg =
            serverCt.UnsafeRegister(static s => ((Socket)s!).Dispose(), socket);

        Task filling = FillPipeAsync(pipe.Writer);

        try
        {
            await ProcessAsync(pipe.Reader, request, response, output);
        }
        catch { /*  */ }
        finally
        {
            try { socket.Shutdown(SocketShutdown.Send); } catch { }
            socket.Dispose();
            try { await pipe.Reader.CompleteAsync(); } catch { }
            try { await filling; } catch { }

            request.Dispose();
            response.Dispose();
            output.Dispose();
        }
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
        while (true)
        {
            ReadResult result = await reader.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;
            SequencePosition consumed = buffer.Start;
            SequencePosition examined = buffer.End;
            bool close = false;

            try
            {
                while (!buffer.IsEmpty)
                {
                    ParseStatus status = HttpParser.TryParse(buffer, request, out SequencePosition end);

                    if (status == ParseStatus.NeedMore) break;

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

                    bool keepAlive = request.KeepAlive;
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