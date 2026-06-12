using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WebStreamCodecPlugin;

/// <summary>
/// Codec-agnostic, non-seekable transport <see cref="Stream"/> for live
/// HTTP audio streams (ICY / Shoutcast / Icecast, or any HTTP source
/// without <c>Range</c> support). Background-pumps raw audio bytes from
/// the network into a bounded in-memory pipe; the codec reads from the
/// pipe at its own pace.
///
/// <para><b>Why a separate Stream class.</b> Codec plugins call our
/// returned Stream's <c>Read</c> on the audio thread, which must not
/// block on network IO. This class hides the latency: a background
/// <see cref="Task"/> drains the HTTP body into <see cref="PipeStream"/>
/// continuously, and the codec's <c>Read</c> consumes from the pipe
/// (only blocking briefly when the network can't keep up with the
/// codec — which would also cause an underrun in any other design).</para>
///
/// <para><b>Disposal.</b> The codec calls <see cref="Dispose"/> on this
/// Stream when playback ends (per the inter-plugin Stream-ownership
/// contract). We cancel the pump, dispose the inner ICY stream and
/// pipe, and wait briefly for the pump task to exit so the HTTP
/// connection actually closes before we return.</para>
///
/// <para><b>CanSeek = false</b> by construction. Codecs that wrap this
/// Stream propagate that to their returned <c>WaveStream.CanSeek</c>
/// (the three first-party codec plugins all do); the host then shows
/// the "● LIVE" badge and disables the loop toggle.</para>
/// </summary>
internal sealed class LiveTransportStream : Stream
{
    // 256 KB ≈ 16 seconds of buffer at 128 kbps. Enough to absorb
    // network jitter and the codec's per-frame bursty reads, small
    // enough not to be a memory concern.
    private const int PipeCapacity = 256 * 1024;

    private readonly IcyAudioStream _icy;
    private readonly PipeStream _pipe;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;
    private long _bytesProduced;
    private bool _disposed;

    /// <summary>Synchronous factory — opens the network stream + spins
    /// up the pump. Throws on initial connect failure (caller decides
    /// what to do — typically translate into a user-visible error).</summary>
    public static LiveTransportStream Open(HttpClient http, string url, CancellationToken ct = default)
    {
        // Synchronous Run-and-wait pattern: we need the connection to
        // succeed (or fail) before returning. The pump task takes over
        // from there.
        var icy = IcyAudioStream.OpenAsync(http, url, ct).GetAwaiter().GetResult();
        return new LiveTransportStream(icy);
    }

    private LiveTransportStream(IcyAudioStream icy)
    {
        _icy = icy;
        _pipe = new PipeStream(PipeCapacity);
        _pumpTask = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        var ct = _cts.Token;
        try
        {
            var buffer = new byte[8192];
            while (!ct.IsCancellationRequested)
            {
                int read = await _icy.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0) break;          // server closed
                _pipe.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) { /* expected on Dispose */ }
        catch (Exception ex)
        {
            // Surface the pump's death to the codec via the pipe — its
            // next Read will throw, which the codec's WaveStream.Read
            // will surface to the host's SafeSampleProvider, which logs
            // and inserts silence.
            _pipe.Fault(ex);
        }
        finally
        {
            // Signal EOF so a Read waiting on data returns 0 (instead
            // of blocking forever).
            _pipe.CompleteWriting();
        }
    }

    // ── Stream interface ────────────────────────────────────────────

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    /// <summary>Returns 0 — codec treats this as "unknown / infinite",
    /// matching the live-stream UI in the host.</summary>
    public override long Length => 0;

    /// <summary>Monotonically increasing byte count consumed from the
    /// pipe. The wrapping codec's decoder pokes at this for elapsed-time
    /// display; returning a sensible counter keeps the host's UI clock
    /// ticking even though seeking is disabled.</summary>
    public override long Position
    {
        get => _bytesProduced;
        set { /* No-op: live streams can't seek. */ }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = _pipe.Read(buffer, offset, count);
        if (n > 0) _bytesProduced += n;
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            // Cancel the pump and dispose the inner ICY stream — this
            // breaks any blocking ReadAsync inside the pump task. Then
            // wait briefly for the task to exit so the HTTP connection
            // actually closes (server-side resources, not just ours).
            _cts.Cancel();
            try { _icy.Dispose(); } catch { /* best-effort */ }
            try { _pumpTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort — never throw from Dispose */ }
            _pipe.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
