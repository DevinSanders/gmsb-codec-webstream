using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;

namespace WebStreamCodecPlugin;

/// <summary>
/// A seekable read-only <see cref="Stream"/> backed by HTTP <c>Range</c>
/// requests against a static URL. This is a format-agnostic transport: the
/// downstream codec plugin's <see cref="Stream"/>-based decoder treats the
/// remote file like a local one — the host's scrub slider works, loops work,
/// duration is reported correctly.
///
/// <para><b>Chunk cache.</b> Each cache miss issues one Range GET for a
/// <see cref="ChunkSize"/>-byte window starting at the current read
/// position. Sequential reads within that window serve from RAM with zero
/// network traffic. Seeks invalidate the cache; the next read pulls a
/// fresh chunk centred on the new position.</para>
///
/// <para><b>Why one chunk, not an LRU?</b> The host wraps every WaveStream
/// in <c>GenericSeekableSampleProvider</c>, which buffers decoded audio
/// ahead of the audio engine. The combination of host pre-buffering + one
/// HTTP chunk worth of byte-cache covers the steady-state case (sequential
/// playback) without any cache pressure. Seeks always cost one network
/// round-trip; that's unavoidable for random-access HTTP playback and is
/// short enough (single Range GET, no metadata exchange) to feel
/// responsive.</para>
///
/// <para><b>CanSeek = true</b>, <see cref="Length"/> known from the HEAD
/// probe at construction time. The decoder builds its frame index over
/// these reads; the resulting <c>WaveStream</c> reports a meaningful
/// duration and the host shows the scrub slider.</para>
/// </summary>
internal sealed class SeekableHttpStream : Stream
{
    // 64 KB per chunk is a deliberate middle ground:
    //   * Big enough that sequential decode (e.g. MP3 frames are ~400 bytes
    //     each at 128 kbps) hits hundreds of cached reads per HTTP request.
    //   * Small enough that a seek-then-decode-one-frame interaction
    //     transfers <100 KB rather than a megabyte.
    private const int ChunkSize = 64 * 1024;

    // 15s per Range GET. We don't want the audio thread blocked
    // indefinitely if a CDN edge stalls; surfacing the IOException lets
    // the host show a clean error rather than freezing.
    private static readonly TimeSpan RangeRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly long _length;
    private long _position;

    // Single-chunk cache. _cacheLength can be < ChunkSize when the chunk
    // straddles end-of-file.
    private readonly byte[] _cache = new byte[ChunkSize];
    private long _cacheStart = -1;
    private int _cacheLength;
    private bool _disposed;

    public SeekableHttpStream(HttpClient http, string url, long length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        _http = http;
        _url = url;
        _length = length;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_position >= _length) return 0;

        // Cache hit: serve as much as we can without crossing the chunk boundary.
        if (_cacheStart >= 0
            && _position >= _cacheStart
            && _position < _cacheStart + _cacheLength)
        {
            int hitOffset = (int)(_position - _cacheStart);
            int available = _cacheLength - hitOffset;
            int toCopy = Math.Min(count, available);
            Buffer.BlockCopy(_cache, hitOffset, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }

        // Cache miss: fetch a chunk anchored at the current position.
        FillCacheAt(_position);

        if (_cacheLength == 0)
            return 0;   // server unexpectedly returned nothing — treat as EOF

        int hit2Offset = (int)(_position - _cacheStart);
        int toCopy2 = Math.Min(count, _cacheLength - hit2Offset);
        Buffer.BlockCopy(_cache, hit2Offset, buffer, offset, toCopy2);
        _position += toCopy2;
        return toCopy2;
    }

    private void FillCacheAt(long startByte)
    {
        long endInclusive = Math.Min(startByte + ChunkSize - 1, _length - 1);
        int expected = (int)(endInclusive - startByte + 1);

        using var cts = new CancellationTokenSource(RangeRequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        request.Headers.Range = new RangeHeaderValue(startByte, endInclusive);

        // HttpClient.Send is synchronous since .NET 5; safe to call from a
        // sync Read. The audio thread sees this as one blocking call per
        // ~64 KB consumed — at 128 kbps MP3 that's a fetch every ~4
        // seconds of playback, well within the host's pre-buffer.
        using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        // Servers that ignored the Range header (returning 200 with the
        // full body) would blow our cache budget; treat that as a hard
        // failure since we can't recover from a server that doesn't honour
        // the contract HEAD said it would.
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            throw new IOException(
                $"Expected 206 Partial Content from Range request to '{_url}', got {(int)response.StatusCode} {response.ReasonPhrase}.");

        using var body = response.Content.ReadAsStream(cts.Token);
        int totalRead = 0;
        while (totalRead < expected)
        {
            int n = body.Read(_cache, totalRead, expected - totalRead);
            if (n <= 0) break;
            totalRead += n;
        }

        _cacheStart = startByte;
        _cacheLength = totalRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (newPos < 0) newPos = 0;
        if (newPos > _length) newPos = _length;
        _position = newPos;
        // Don't invalidate the cache — the next Read will reuse it if the
        // seek landed inside the current chunk (common for a decoder's
        // frame-index scan, which pokes back and forth in a small window).
        return _position;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // We don't own _http (it's the plugin's shared client), so there's
        // nothing network-side to close here — but flipping _disposed makes
        // use-after-dispose fail loudly instead of silently issuing a
        // Range GET against a transport the codec already tore down.
        _disposed = true;
        base.Dispose(disposing);
    }
}
