using System.Buffers;
using System.Text;

namespace DogRoute.Server.Tests.Support;

internal static class Seq
{
    public static ReadOnlySequence<byte> Of(string ascii) => new(Encoding.ASCII.GetBytes(ascii));

    public static ReadOnlySequence<byte> Segmented(string ascii, int chunkSize)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(ascii);
        Segment? first = null;
        Segment? last = null;

        for (int i = 0; i < bytes.Length; i += chunkSize)
        {
            var seg = new Segment(bytes.AsMemory(i, Math.Min(chunkSize, bytes.Length - i)));
            if (last is null) first = seg;
            else last.Append(seg);
            last = seg;
        }

        if (first is null || last is null) return ReadOnlySequence<byte>.Empty;
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public void Append(Segment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}
