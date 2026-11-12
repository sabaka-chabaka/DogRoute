namespace DogRoute.Server.Tests;

public class ByteBufferTests
{
    [Fact]
    public void Write_appends_data()
    {
        using var buf = new ByteBuffer(16);

        buf.Write("abc"u8);
        buf.Write("def"u8);

        Assert.Equal(6, buf.Count);
        Assert.True(buf.WrittenSpan.SequenceEqual("abcdef"u8));
    }

    [Fact]
    public void Grows_and_keeps_existing_content()
    {
        using var buf = new ByteBuffer(4);
        byte[] data = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();

        buf.Write(data);

        Assert.Equal(1000, buf.Count);
        Assert.True(buf.WrittenSpan.SequenceEqual(data.AsSpan()));
    }

    [Fact]
    public void Clear_resets_count_and_allows_reuse()
    {
        using var buf = new ByteBuffer(16);
        buf.Write("abc"u8);

        buf.Clear();
        buf.Write("xy"u8);

        Assert.Equal(2, buf.Count);
        Assert.True(buf.WrittenSpan.SequenceEqual("xy"u8));
    }

    [Fact]
    public void Advance_beyond_capacity_throws()
    {
        using var buf = new ByteBuffer(16);

        Assert.Throws<ArgumentOutOfRangeException>(() => buf.Advance(int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.Advance(-1));
    }
}
