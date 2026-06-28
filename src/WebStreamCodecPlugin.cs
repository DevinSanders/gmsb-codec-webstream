using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using NAudio.Wave;
using SoundBoard.PluginApi;

namespace WebStreamCodecPlugin;

/// <summary>
/// <see cref="IAudioCodecPlugin"/> for URL-based audio streaming over
/// HTTP / HTTPS — internet radio, podcast-style progressive downloads,
/// any HTTP(S) audio URL. Cross-platform (Windows / macOS / Linux), no
/// native dependencies, **no bundled decoder library**.
///
/// <para><b>Inter-plugin dispatch.</b> This plugin is a transport layer
/// only. It opens the HTTP body and probes seekability; it does NOT
/// decode. Decoding is delegated to whichever format-specific codec
/// plugin the user has installed — <c>codec.mp3</c>, <c>codec.ogg</c>,
/// <c>codec.flac</c>, … — via the host's <see cref="IAudioCodecRegistry"/>
/// (exposed on <see cref="IPluginContext.CodecRegistry"/>).</para>
///
/// <para><b>URL resolution.</b> Some URLs aren't directly playable —
/// YouTube watch pages return HTML, not audio. <see cref="ResolveUrl"/>
/// runs upstream of the HEAD probe and rewrites those URLs to a direct
/// CDN URL via <see cref="YouTubeResolver"/>. Most URLs pass through
/// unchanged. Future resolvers (PLS/M3U playlists, HLS, …) slot in
/// alongside YouTube without touching the rest of the pipeline.</para>
///
/// <para><b>Per-URL routing.</b> Every URL (post-resolution) gets a HEAD
/// probe to decide seekability AND Content-Type. Routing:
/// <list type="bullet">
///   <item><description>Server advertises <c>Accept-Ranges: bytes</c>
///   with a known <c>Content-Length</c> → <see cref="SeekableHttpStream"/>
///   as the transport (Range-request-backed cache). The chosen codec
///   reports <c>CanSeek = true</c>: host shows scrub + loop.</description></item>
///   <item><description>Anything else (live ICY / Shoutcast, server
///   without Range support, HEAD failed) → <see cref="LiveTransportStream"/>
///   (forward-only HTTP body + bounded-buffer pump). The chosen codec
///   reports <c>CanSeek = false</c>: host shows the "● LIVE" badge and
///   disables loop.</description></item>
/// </list></para>
///
/// <para><b>No bundled decoder.</b> If the user installs this plugin
/// without any codec that accepts Stream input, every URL fails with a
/// clear error pointing at <c>docs/PLUGINS.md</c>. The companion
/// <c>codec.mp3</c> / <c>codec.ogg</c> / <c>codec.flac</c> plugins all
/// implement the Stream overload and register MIME types — install one
/// of those alongside this plugin to enable the corresponding format.</para>
///
/// <para><b>Threading.</b>
/// <list type="bullet">
///   <item>Initialize / Shutdown — UI thread. Lazy-initialises the shared
///     <see cref="HttpClient"/>, captures the registry from the context.</item>
///   <item>CreateStream — UI thread. Runs the HEAD probe (5 s timeout) +
///     opens the transport. Host shows a loading state during this.</item>
///   <item>The returned WaveStream's Read runs on the audio thread at
///     48 kHz. Both transports buffer ahead so the audio callback never
///     does blocking network IO.</item>
/// </list></para>
/// </summary>
public sealed class WebStreamCodecPlugin : IAudioCodecPlugin
{
    private static readonly Lazy<HttpClient> s_http = new(CreateHttpClient);

    // Capture the IPluginContext, NOT the registry. The host
    // (SoundBoard.Core PluginContext) creates the context with a null
    // CodecRegistry, calls every plugin's Initialize, then — only after
    // all plugins finish loading — mutates each context to attach the
    // populated IAudioCodecRegistry. Capturing context.CodecRegistry during
    // Initialize would freeze us against the null. Reading through the
    // context lazily at CreateStream time sees the populated registry.
    private IPluginContext? _context;

    // Test seam: unit tests inject an HttpClient backed by a fake message
    // handler so the probe + transports can be exercised without a real
    // network. Null in production — falls back to the shared client.
    internal static HttpClient? HttpClientOverride { get; set; }
    private static HttpClient Http => HttpClientOverride ?? s_http.Value;

    // Test seam: lets unit tests stub the YouTube resolver so dispatch
    // wiring around CreateStream can be exercised without calling out to
    // youtube.com. Null in production — falls back to the real YoutubeExplode
    // path in YouTubeResolver.
    internal static Func<HttpClient, string, string>? YouTubeResolverOverride { get; set; }

    public string Id => "codec.webstream";
    public string Name => "Web Stream Codec";
    public string Description => "Cross-platform URL-based audio streaming (YouTube, internet radio, progressive HTTP audio). YouTube page URLs are resolved to direct CDN URLs via YoutubeExplode; decoding is delegated to installed codec plugins (MP3, OGG, FLAC, WebM, …). Seekability detected per-URL via HEAD probe.";
    public string Version => PluginVersion.OfAssembly(typeof(WebStreamCodecPlugin));
    public string Author => "Devin Sanders";

    public IEnumerable<string> SupportedPatterns => new[] { "http://", "https://" };

    // We don't accept Stream input — we ARE the transport for everyone
    // else. Leave SupportsStreamInput at the default false.

    public void Initialize(IPluginContext context)
    {
        // Capture the context, not the registry. The host attaches the
        // codec registry to this same context instance AFTER all plugins
        // finish loading — see the field-level comment for why we must
        // dereference lazily.
        _context = context;
    }

    public void Shutdown() { }

    public WaveStream CreateStream(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Stream URL must not be empty.", nameof(source));

        // Read the registry lazily — it's attached to the IPluginContext
        // by the host AFTER every plugin's Initialize completes. Reading
        // it now (CreateStream time) sees the populated snapshot.
        var registry = _context?.CodecRegistry
            ?? throw new InvalidOperationException(
                "codec.webstream needs the host's codec registry to dispatch decode work. " +
                "Either Initialize(IPluginContext) was never called, or the host's " +
                "IPluginContext.CodecRegistry is still null at CreateStream time " +
                "(it should be populated once all codec plugins finish loading).");

        // 1. URL resolution. Most URLs pass through unchanged, but some
        //    "container" URL types (YouTube page URLs being the canonical
        //    example) point at HTML rather than audio bytes and need
        //    rewriting to a direct CDN URL before the rest of the pipeline
        //    can do anything with them. Resolvers handle that asymmetry
        //    upstream of the HEAD probe so downstream steps stay generic.
        var resolvedUrl = ResolveUrl(Http, source);

        // 2. HEAD probe — drives both the seekability and codec-selection
        //    decisions. Conservative: anything that doesn't EXPLICITLY
        //    look seekable falls through to the live path.
        var probe = HttpProbe.Run(Http, resolvedUrl);

        // 3. Pick the codec via the registry, by MIME first then URL
        //    extension. If we can't resolve a codec, throw a useful
        //    error pointing the user at the catalog.
        var codec = ResolveCodec(registry, resolvedUrl, probe.ContentType);

        // 4. Open the transport (seekable vs live) and hand it to the
        //    codec. Ownership of the transport Stream transfers — the
        //    codec's WaveStream.Dispose() will dispose our transport.
        Stream transport;
        if (probe.LooksSeekable)
        {
            transport = new SeekableHttpStream(Http, resolvedUrl, probe.ContentLength!.Value);
        }
        else
        {
            transport = LiveTransportStream.Open(Http, resolvedUrl);
        }

        // 5. The format hint helps codecs that handle multiple variants
        //    pick the right path. MIME wins over extension when both are
        //    available; falls back to extension parsed from the URL path.
        var hint = !string.IsNullOrEmpty(probe.ContentType)
            ? probe.ContentType!
            : ExtensionFromUrl(resolvedUrl);

        try
        {
            return codec.CreateStream(transport, hint);
        }
        catch
        {
            // Codec construction failed — dispose the transport we just
            // opened. Without this we'd leak the HTTP connection until GC.
            try { transport.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>Map a possibly-indirect URL (a YouTube page, etc.) to a
    /// direct media URL the HEAD probe + transports can consume. Most
    /// URLs pass through unchanged.
    ///
    /// <para>Today this is one branch (YouTube via <see cref="YouTubeResolver"/>)
    /// — future resolvers (PLS/M3U playlists, HLS master manifests, …)
    /// would slot in here alongside it. The signature stays
    /// <c>(string) -&gt; string</c> so the rest of <see cref="CreateStream"/>
    /// doesn't have to know which kind of URL it started with.</para>
    ///
    /// <para>Internal for test access via the
    /// <see cref="YouTubeResolverOverride"/> seam — tests stub the YouTube
    /// branch so dispatch wiring can be exercised without hitting
    /// youtube.com.</para></summary>
    internal static string ResolveUrl(HttpClient http, string source)
    {
        if (YouTubeResolver.IsYouTubeUrl(source))
        {
            var stub = YouTubeResolverOverride;
            return stub is null ? YouTubeResolver.Resolve(http, source) : stub(http, source);
        }
        return source;
    }

    /// <summary>Look up a <b>Stream-capable</b> codec for this URL. Prefers
    /// Content-Type from the HEAD response (more reliable); falls back
    /// to the URL's file extension; finally throws a user-actionable
    /// error.
    ///
    /// <para>This walks <see cref="IAudioCodecRegistry.All"/> directly
    /// rather than calling <c>GetByContentType</c> / <c>GetByExtension</c>
    /// because we have an additional requirement those don't enforce:
    /// the codec must implement <see cref="IAudioCodecPlugin.SupportsStreamInput"/>.
    /// A codec like <c>codec.aac.ffmpeg</c> registers MIME types and
    /// extensions for file-based AAC decode but doesn't (yet) implement
    /// the Stream overload — the convenience helpers would return it
    /// and then we'd throw at dispatch time, masking the next-in-line
    /// codec that COULD have handled the URL. Walking with a
    /// <c>SupportsStreamInput</c> filter avoids that trap.</para></summary>
    internal static IAudioCodecPlugin ResolveCodec(IAudioCodecRegistry registry, string url, string? contentType)
    {
        // Try MIME first (more reliable than extension parsing).
        IAudioCodecPlugin? codec = null;
        if (!string.IsNullOrWhiteSpace(contentType))
            codec = FindStreamCapableByMime(registry, contentType!);

        if (codec is null)
        {
            var ext = ExtensionFromUrl(url);
            if (!string.IsNullOrEmpty(ext))
                codec = FindStreamCapableByExtension(registry, ext);
        }

        if (codec is null)
        {
            // Build a helpful error. If we found a non-Stream-capable
            // codec for this MIME/extension, name it — that tells the
            // user that AAC support (for example) is installed but only
            // for files, not URLs, and saves them a fruitless search.
            var nonStream = !string.IsNullOrWhiteSpace(contentType)
                ? registry.GetByContentType(contentType!)
                : null;
            if (nonStream is null)
            {
                var ext = ExtensionFromUrl(url);
                if (!string.IsNullOrEmpty(ext)) nonStream = registry.GetByExtension(ext);
            }

            var extraHint = nonStream is { SupportsStreamInput: false }
                ? $" The codec '{nonStream.Id}' matches but doesn't yet implement Stream input " +
                  "— file-based decode only. Track its release notes for an update."
                : string.Empty;

            throw new NotSupportedException(
                $"No Stream-capable codec handles the stream at '{url}'" +
                (contentType is not null ? $" (Content-Type: '{contentType}')" : "") +
                ". Install the matching codec plugin — e.g. gmsb-codec-mp3 for 'audio/mpeg', " +
                "gmsb-codec-ogg for 'audio/ogg', gmsb-codec-flac for 'audio/flac'. " +
                "See docs/PLUGINS.md in the main repo for the catalog." + extraHint);
        }

        return codec;
    }

    internal static IAudioCodecPlugin? FindStreamCapableByMime(IAudioCodecRegistry registry, string contentType)
    {
        // Strip any "; charset=..." parameter — the registry's
        // GetByContentType does the same, but we're matching by hand.
        var semi = contentType.IndexOf(';');
        if (semi >= 0) contentType = contentType[..semi];
        contentType = contentType.Trim();

        foreach (var c in registry.All)
        {
            if (!c.SupportsStreamInput) continue;
            var mimes = c.SupportedContentTypes;
            if (mimes is null) continue;
            foreach (var m in mimes)
            {
                if (string.Equals(m, contentType, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
        }
        return null;
    }

    internal static IAudioCodecPlugin? FindStreamCapableByExtension(IAudioCodecRegistry registry, string extension)
    {
        if (extension[0] != '.') extension = "." + extension;
        foreach (var c in registry.All)
        {
            if (!c.SupportsStreamInput) continue;
            var pats = c.SupportedPatterns;
            if (pats is null) continue;
            foreach (var p in pats)
            {
                if (string.IsNullOrEmpty(p) || p[0] != '.') continue;
                if (string.Equals(p, extension, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
        }
        return null;
    }

    /// <summary>Pull the file extension off a URL's path component.
    /// Returns a leading-dot string (e.g. <c>".mp3"</c>) or empty when
    /// the URL has none.</summary>
    internal static string ExtensionFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url, UriKind.Absolute);
            return Path.GetExtension(uri.AbsolutePath);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        // Follow CDN redirects (common with radio aggregator portals like
        // SomaFM that hand out PLS files pointing at edge servers).
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            // Keep connections warm — Range GETs against the same host
            // benefit from reused TLS sessions.
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            // No global timeout — live ICY streams are intentionally infinite.
            // Individual operations enforce their own deadlines via CancellationToken.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        // Some servers reject .NET's default User-Agent. Identify ourselves
        // plainly so server-side logs are useful and rate-limiting works
        // against this plugin specifically rather than "any .NET app".
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GMSB-WebStreamCodec/2.0");
        return client;
    }
}
