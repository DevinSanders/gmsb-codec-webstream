using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WebStreamCodecPlugin;

/// <summary>
/// A <see cref="Stream"/> wrapper around an HTTP body that strips Shoutcast /
/// Icecast (ICY) metadata frames if the server interleaves them.
///
/// <para><b>The ICY framing.</b> When a client sends <c>Icy-MetaData: 1</c>,
/// a metadata-enabled server responds with <c>icy-metaint: N</c> meaning:
/// "every N bytes of audio is followed by 1 byte that encodes the metadata
/// block length in 16-byte units, then that many bytes of metadata." We
/// have to skip those metadata bytes so the audio decoder sees only frames.
/// Servers that omit <c>icy-metaint</c> return raw audio with no
/// interleaving; this stream then degenerates to a passthrough.</para>
///
/// <para>Reads from this stream block on the underlying HTTP body. The caller
/// (<see cref="LiveTransportStream"/>) pumps it on a background task so the
/// audio thread never sees the latency.</para>
/// </summary>
internal sealed class IcyAudioStream : Stream
{
    private readonly Stream _inner;
    private readonly int _metaInterval;       // bytes of audio between metadata blocks; 0 = no metadata
    private int _bytesUntilMeta;              // countdown to the next metadata block

    public IcyAudioStream(Stream inner, int metaInterval)
    {
        _inner = inner;
        _metaInterval = metaInterval;
        _bytesUntilMeta = metaInterval;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;

        // No interleaving — passthrough.
        if (_metaInterval == 0)
            return _inner.Read(buffer, offset, count);

        // Don't cross a metadata boundary in a single read: cap at the
        // remaining audio bytes in the current chunk so the next read sees
        // the metadata-length byte exactly where we expect it.
        int allowed = Math.Min(count, _bytesUntilMeta);
        int read = _inner.Read(buffer, offset, allowed);
        if (read <= 0) return read;

        _bytesUntilMeta -= read;
        if (_bytesUntilMeta == 0)
            ConsumeMetadataBlock();

        return read;
    }

    /// <summary>
    /// Read and discard one metadata block. Block format: 1 length byte (in
    /// 16-byte units, so 0..255 → 0..4080 bytes), followed by that many
    /// bytes of UTF-8 (typically <c>StreamTitle='…';StreamUrl='…';</c>).
    /// We don't surface the title to the host today — could be a future
    /// enhancement to set the playing-card subtitle.
    /// </summary>
    private void ConsumeMetadataBlock()
    {
        int lengthByte = _inner.ReadByte();
        if (lengthByte < 0)
        {
            // EOF right where a metadata block should start — drop into
            // passthrough mode rather than blowing up. The decoder will
            // see EOF on its next read.
            _bytesUntilMeta = int.MaxValue;
            return;
        }

        int metaBytes = lengthByte * 16;
        if (metaBytes > 0)
        {
            // Drain. Discarding to a stack-allocated buffer keeps allocations
            // off the GC path, but Read here is on the background pump thread,
            // not the audio thread, so even a heap buffer would be safe.
            Span<byte> scratch = stackalloc byte[256];
            while (metaBytes > 0)
            {
                int chunk = Math.Min(scratch.Length, metaBytes);
                int got = _inner.Read(scratch[..chunk]);
                if (got <= 0) break;
                metaBytes -= got;
            }
        }

        _bytesUntilMeta = _metaInterval;
    }

    /// <summary>
    /// Opens an HTTP GET to the URL with ICY metadata enabled, examines the
    /// response headers, and returns a stream that yields raw audio bytes
    /// (metadata frames already stripped if present).
    /// </summary>
    public static async Task<IcyAudioStream> OpenAsync(HttpClient http, string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Ask for metadata; servers that don't speak ICY ignore the header.
        request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");

        // ResponseHeadersRead so we don't buffer the whole stream into memory
        // before we get headers — critical for an infinite live stream.
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        int metaInt = 0;
        if (response.Headers.TryGetValues("icy-metaint", out var values))
        {
            foreach (var v in values)
            {
                if (int.TryParse(v, out var parsed) && parsed > 0)
                {
                    metaInt = parsed;
                    break;
                }
            }
        }

        var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return new IcyAudioStream(body, metaInt);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
