using System;
using System.Net.Http;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace WebStreamCodecPlugin;

/// <summary>
/// Detects YouTube page URLs and resolves them to direct CDN URLs the rest
/// of the plugin's HEAD-probe / transport / dispatch pipeline can handle as
/// ordinary HTTP audio.
///
/// <para><b>Why this is needed.</b> <c>https://www.youtube.com/watch?v=…</c>
/// serves an HTML page, not audio bytes. Without resolving first, the HEAD
/// probe sees <c>text/html</c> and <see cref="WebStreamCodecPlugin.ResolveCodec"/>
/// has nothing to dispatch to. After resolution, the URL points at a video
/// CDN that serves audio bytes (Opus-in-WebM or AAC-in-MP4) with Range
/// support — the usual transport-and-codec path takes over from there.</para>
///
/// <para><b>Format-codec dependency.</b> Resolution alone isn't enough — a
/// codec plugin claiming the resolved CDN's Content-Type must be installed:
/// <list type="bullet">
///   <item><c>gmsb-codec-webm</c> for the WebM-Opus path (audio/webm) —
///   this is what YoutubeExplode picks by default via highest-bitrate
///   audio-only.</item>
///   <item>A future stream-capable AAC codec for the audio/mp4 path.</item>
/// </list>
/// If no codec matches the resolved Content-Type, the usual catalog-pointing
/// <see cref="NotSupportedException"/> from <c>ResolveCodec</c> fires.</para>
///
/// <para><b>Reliability.</b> YouTube periodically changes its internal API
/// shape, which can break YoutubeExplode until it ships a patched release.
/// When that happens, <see cref="Resolve"/> throws
/// <see cref="InvalidOperationException"/> with a message naming the URL
/// — the host surfaces it as a load failure on the playing-card.</para>
/// </summary>
internal static class YouTubeResolver
{
    /// <summary>Host-based detection — no network call. Matches
    /// <c>youtu.be</c> and <c>youtube.com</c> plus any subdomain
    /// (<c>www.</c>, <c>music.</c>, <c>m.</c>, …). Lives separately from
    /// <see cref="Resolve"/> so the plugin can decide cheaply whether to
    /// take the resolver branch at all.
    ///
    /// <para>Substring checks were tempting but matched
    /// <c>notyoutube.com/watch</c>. Parsing the URL and comparing the host
    /// explicitly is the only reliable approach.</para></summary>
    public static bool IsYouTubeUrl(string s)
    {
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        return string.Equals(host, "youtu.be", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resolve a YouTube page URL to a direct CDN URL. Picks the
    /// highest-bitrate audio-only stream (typically Opus-in-WebM).
    /// Synchronous because <c>CreateStream</c> is called on the UI thread
    /// during the host's "loading…" state and the cost (one HTTPS
    /// handshake + manifest fetch) is bounded.
    ///
    /// <para><b>Why the <c>Task.Run</c> hop.</b> <c>CreateStream</c> runs
    /// on the host's UI thread (Avalonia <c>AvaloniaSynchronizationContext</c>).
    /// YoutubeExplode is a third-party library — we can't guarantee its
    /// internal <c>await</c>s use <c>ConfigureAwait(false)</c>, so any one
    /// of them may try to resume on the UI thread. If we did
    /// <c>GetManifestAsync(...).GetAwaiter().GetResult()</c> directly on
    /// the UI thread, the resume would deadlock against our own block:
    /// the UI thread is busy waiting on the task, the task is waiting for
    /// the UI thread. Symptom: <c>CreateStream</c> never returns, playback
    /// hangs silently with no error. Pushing the await onto a thread-pool
    /// thread via <c>Task.Run</c> means the captured context (if any) is
    /// the pool's, not the UI's, so the continuations resolve normally.
    /// Our own async code (<see cref="IcyAudioStream.OpenAsync"/>) uses
    /// <c>ConfigureAwait(false)</c> throughout and doesn't need this hop.</para></summary>
    public static string Resolve(HttpClient http, string url)
    {
        // YoutubeClient can be given an HttpClient — reuse the plugin's
        // shared one so connection pooling + redirects behave the same as
        // for any other URL the plugin handles.
        var youtube = new YoutubeClient(http);

        StreamManifest manifest;
        try
        {
            // GetAwaiter().GetResult() (not .Result) unwraps inner
            // exceptions instead of wrapping them in AggregateException,
            // so the catch below sees YoutubeExplode's original exception.
            manifest = Task.Run(() => youtube.Videos.Streams.GetManifestAsync(url).AsTask())
                           .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Wrap so the host's error surface points at "the URL" rather
            // than at YoutubeExplode's internals (which most users won't
            // recognize).
            throw new InvalidOperationException(
                $"Failed to resolve YouTube URL '{url}'. The video may be private, " +
                "age-restricted, geo-blocked, or YouTube may have changed its API " +
                "(in which case a YoutubeExplode update is required).", ex);
        }

        var audio = manifest.GetAudioOnlyStreams().TryGetWithHighestBitrate()
            ?? throw new InvalidOperationException(
                $"YouTube URL '{url}' has no audio-only stream available.");

        // audio.Url is a CDN URL that supports Range + reports a real
        // Content-Type (typically audio/webm for Opus, audio/mp4 for AAC).
        return audio.Url;
    }
}
