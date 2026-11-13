using System.Text.Unicode;

namespace DogRoute.Server;

internal static class UriPath
{
    public static bool NeedsNormalization(ReadOnlySpan<byte> path) =>
        path.IndexOf((byte)'%') >= 0 || path.IndexOf("/."u8) >= 0;

    public static bool TryNormalize(ReadOnlySpan<byte> raw, Span<byte> destination, out int length)
    {
        length = 0;
        if (raw.IsEmpty || destination.Length < raw.Length) return false;

        int decodedLength = PercentDecode(raw, destination);
        if (decodedLength < 0) return false;

        Span<byte> decoded = destination[..decodedLength];

        if (decoded.IndexOf((byte)0) >= 0) return false;
        if (decoded[0] != (byte)'/') return false;
        if (!Utf8.IsValid(decoded)) return false;

        int normalized = RemoveDotSegments(decoded);
        if (normalized < 0) return false;

        length = normalized;
        return true;
    }

    internal static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    private static int PercentDecode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int written = 0;

        for (int i = 0; i < src.Length; i++)
        {
            byte b = src[i];

            if (b == (byte)'%')
            {
                if (i + 2 >= src.Length) return -1;

                int hi = HexValue(src[i + 1]);
                int lo = HexValue(src[i + 2]);
                if (hi < 0 || lo < 0) return -1;

                b = (byte)((hi << 4) | lo);
                i += 2;
            }

            dst[written++] = b;
        }

        return written;
    }

    private static int RemoveDotSegments(Span<byte> p)
    {
        int write = 0;
        int i = 0;

        while (i < p.Length)
        {
            int start = i + 1;
            int end = start;
            while (end < p.Length && p[end] != (byte)'/') end++;
            int segmentLength = end - start;

            if (segmentLength == 1 && p[start] == (byte)'.')
            {
                if (end == p.Length) p[write++] = (byte)'/';
            }
            else if (segmentLength == 2 && p[start] == (byte)'.' && p[start + 1] == (byte)'.')
            {
                if (write == 0) return -1;

                write--;
                while (write > 0 && p[write] != (byte)'/') write--;

                if (end == p.Length) p[write++] = (byte)'/';
            }
            else
            {
                p.Slice(i, end - i).CopyTo(p[write..]);
                write += end - i;
            }

            i = end;
        }

        return write;
    }
}
