using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace WebStreamCodecPlugin.Tests;

// ── PipeStream ────────────────────────────────────────────────────────

public class PipeStreamTests
{
    [Fact]
    public void Write_then_read_round_trips_bytes()
    {
        using var pipe = new PipeStream(1024);
        var data = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        pipe.Write(data, 0, data.Length);

        var buf = new byte[200];
        int read = ReadFully(pipe, buf, 200);

        read.Should().Be(200);
        buf.Should().Equal(data);
    }

    [Fact]
    public void Read_wraps_around_the_ring_buffer()
    {
        // Capacity 16: write 12, read 12, then write 8 — the second write
        // straddles the end of the backing array and must reassemble correctly.
        using var pipe = new PipeStream(16);
        var first = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
        pipe.Write(first, 0, first.Length);
        var sink = new byte[12];
        ReadFully(pipe, sink, 12);

        var second = Enumerable.Range(100, 8).Select(i => (byte)i).ToArray();
        pipe.Write(second, 0, second.Length);
        var sink2 = new byte[8];
        ReadFully(pipe, sink2, 8);

        sink2.Should().Equal(second);
    }

    [Fact]
    public void Read_returns_zero_after_writing_completes_and_buffer_drains()
    {
        using var pipe = new PipeStream(64);
        pipe.Write(new byte[10], 0, 10);
        pipe.CompleteWriting();

        var buf = new byte[10];
        pipe.Read(buf, 0, 10).Should().Be(10);
        pipe.Read(buf, 0, 10).Should().Be(0, "EOF once writing completed and the buffer is empty");
    }

    [Fact]
    public void Fault_surfaces_as_IOException_on_read()
    {
        using var pipe = new PipeStream(64);
        pipe.Fault(new TimeoutException("boom"));

        pipe.Invoking(p => p.Read(new byte[10], 0, 10))
            .Should().Throw<IOException>().WithInnerException<TimeoutException>();
    }

    [Fact]
    public async Task Read_blocks_until_data_arrives_then_unblocks()
    {
        using var pipe = new PipeStream(64);
        var reader = Task.Run(() =>
        {
            var buf = new byte[4];
            return pipe.Read(buf, 0, 4);
        });

        reader.IsCompleted.Should().BeFalse("no data yet — reader should be parked");
        pipe.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);

        (await reader).Should().Be(4);
    }

    private static int ReadFully(Stream s, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buffer, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}

// ── IcyAudioStream (metadata stripping) ───────────────────────────────

public class IcyAudioStreamTests
{
    [Fact]
    public void MetaInterval_zero_is_a_passthrough()
    {
        var audio = Enumerable.Range(0, 500).Select(i => (byte)i).ToArray();
        using var icy = new IcyAudioStream(new MemoryStream(audio), metaInterval: 0);

        var outBuf = new byte[500];
        int read = ReadFully(icy, outBuf, 500);

        read.Should().Be(500);
        outBuf.Should().Equal(audio);
    }

    [Fact]
    public void Strips_interleaved_metadata_block()
    {
        // Layout: [16 audio bytes][1 length byte = 1 → 16 meta bytes][16 meta][16 audio]
        const int interval = 16;
        var audio1 = Enumerable.Repeat((byte)0xAA, interval).ToArray();
        var audio2 = Enumerable.Repeat((byte)0xBB, interval).ToArray();
        // Exactly 16 bytes of metadata (content is irrelevant — it's discarded).
        var meta = Enumerable.Repeat((byte)0x7E, 16).ToArray();

        using var raw = new MemoryStream();
        raw.Write(audio1);
        raw.WriteByte(1);            // 1 * 16 = 16 metadata bytes follow
        raw.Write(meta);
        raw.Write(audio2);
        raw.Position = 0;

        using var icy = new IcyAudioStream(raw, interval);
        var outBuf = new byte[interval * 2];
        int read = ReadFully(icy, outBuf, outBuf.Length);

        read.Should().Be(interval * 2, "the 16 metadata bytes are stripped, leaving only audio");
        outBuf.Take(interval).Should().Equal(audio1);
        outBuf.Skip(interval).Should().Equal(audio2);
    }

    [Fact]
    public void Zero_length_metadata_block_is_handled()
    {
        // A length byte of 0 means "no metadata this round" — common when
        // the title hasn't changed. Must not consume any audio bytes for it.
        const int interval = 8;
        var audio1 = Enumerable.Repeat((byte)0x11, interval).ToArray();
        var audio2 = Enumerable.Repeat((byte)0x22, interval).ToArray();

        using var raw = new MemoryStream();
        raw.Write(audio1);
        raw.WriteByte(0);            // zero-length metadata
        raw.Write(audio2);
        raw.Position = 0;

        using var icy = new IcyAudioStream(raw, interval);
        var outBuf = new byte[interval * 2];
        ReadFully(icy, outBuf, outBuf.Length).Should().Be(interval * 2);
        outBuf.Take(interval).Should().Equal(audio1);
        outBuf.Skip(interval).Should().Equal(audio2);
    }

    [Fact]
    public void CanSeek_is_false()
    {
        using var icy = new IcyAudioStream(new MemoryStream(), 0);
        icy.CanSeek.Should().BeFalse();
    }

    private static int ReadFully(Stream s, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buffer, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}

// ── SeekableHttpStream (Range transport) ──────────────────────────────

public class SeekableHttpStreamTests
{
    private static byte[] MakeData(int n) => Enumerable.Range(0, n).Select(i => (byte)(i % 251)).ToArray();

    [Fact]
    public void Ctor_rejects_nonpositive_length()
    {
        using var http = new FakeHttpMessageHandler(_ => throw new InvalidOperationException()).ToClient();
        FluentActions.Invoking(() => new SeekableHttpStream(http, "http://h/x", 0))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Reports_seekable_with_known_length()
    {
        using var http = new FakeHttpMessageHandler(_ => throw new InvalidOperationException()).ToClient();
        using var s = new SeekableHttpStream(http, "http://h/x", 1234);
        s.CanSeek.Should().BeTrue();
        s.Length.Should().Be(1234);
    }

    [Fact]
    public void Read_fetches_bytes_via_range_request()
    {
        var data = MakeData(100_000);
        var handler = new FakeHttpMessageHandler(req => FakeResponses.PartialContent(data, req));
        using var http = handler.ToClient();
        using var s = new SeekableHttpStream(http, "http://h/x", data.Length);

        var buf = new byte[5000];
        int read = s.Read(buf, 0, buf.Length);

        read.Should().BeGreaterThan(0);
        buf.Take(read).Should().Equal(data.Take(read));
        handler.Requests.Should().ContainSingle()
            .Which.Range.Should().NotBeNull("Read must issue a Range request");
    }

    [Fact]
    public void Sequential_reads_within_a_chunk_reuse_the_cache()
    {
        var data = MakeData(100_000);
        var handler = new FakeHttpMessageHandler(req => FakeResponses.PartialContent(data, req));
        using var http = handler.ToClient();
        using var s = new SeekableHttpStream(http, "http://h/x", data.Length);

        // Two small reads that both land inside the first 64 KB chunk →
        // only one HTTP request.
        var buf = new byte[1000];
        s.Read(buf, 0, 1000).Should().Be(1000);
        s.Read(buf, 0, 1000).Should().Be(1000);

        handler.Requests.Count.Should().Be(1, "the second read is served from the single-chunk cache");
    }

    [Fact]
    public void Seek_clamps_into_range()
    {
        using var http = new FakeHttpMessageHandler(_ => throw new InvalidOperationException()).ToClient();
        using var s = new SeekableHttpStream(http, "http://h/x", 1000);

        s.Seek(-50, SeekOrigin.Begin);
        s.Position.Should().Be(0);

        s.Seek(5000, SeekOrigin.Begin);
        s.Position.Should().Be(1000);

        s.Position = 400;
        s.Seek(100, SeekOrigin.Current);
        s.Position.Should().Be(500);
    }

    [Fact]
    public void Non_206_response_throws_IOException()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.ByteArrayContent(new byte[10]),
            });
        using var http = handler.ToClient();
        using var s = new SeekableHttpStream(http, "http://h/x", 1000);

        s.Invoking(x => x.Read(new byte[10], 0, 10))
            .Should().Throw<IOException>().WithMessage("*206*");
    }

    [Fact]
    public void Read_after_dispose_throws_ObjectDisposed()
    {
        using var http = new FakeHttpMessageHandler(_ => throw new InvalidOperationException()).ToClient();
        var s = new SeekableHttpStream(http, "http://h/x", 1000);
        s.Dispose();

        s.Invoking(x => x.ReadByte()).Should().Throw<ObjectDisposedException>();
    }
}
