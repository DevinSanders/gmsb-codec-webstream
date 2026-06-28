using System;
using System.IO;
using System.Net.Http;
using FluentAssertions;
using Xunit;

namespace WebStreamCodecPlugin.Tests;

// All tests that mutate the static HttpClientOverride live in one class so
// xunit runs them sequentially (tests within a class share a collection),
// avoiding cross-test contamination of the static seam.
public sealed class WebStreamCodecPluginTests : IDisposable
{
    private readonly WebStreamCodecPlugin _plugin = new();

    public void Dispose()
    {
        WebStreamCodecPlugin.HttpClientOverride = null;
        WebStreamCodecPlugin.YouTubeResolverOverride = null;
    }

    private static WebStreamCodecPlugin WithRegistry(params SoundBoard.PluginApi.IAudioCodecPlugin[] codecs)
    {
        var plugin = new WebStreamCodecPlugin();
        plugin.Initialize(new FakeContext(new FakeRegistry(codecs)));
        return plugin;
    }

    private static FakeCodec Mp3StreamCodec() =>
        new("codec.mp3", new[] { ".mp3" }, new[] { "audio/mpeg", "audio/mp3" }, supportsStream: true);

    // ── Declarations ──────────────────────────────────────────────────

    [Fact]
    public void Id_is_codec_webstream() => _plugin.Id.Should().Be("codec.webstream");

    [Fact]
    public void SupportedPatterns_claims_http_and_https()
        => _plugin.SupportedPatterns.Should().Contain(new[] { "http://", "https://" });

    [Fact]
    public void SupportsStreamInput_is_false_we_are_the_transport_not_a_decoder()
        // Default-interface member — the plugin doesn't override it, so read
        // it through the interface (which is how the host sees it too).
        => ((SoundBoard.PluginApi.IAudioCodecPlugin)_plugin).SupportsStreamInput.Should().BeFalse();

    [Fact]
    public void SupportedContentTypes_is_empty_we_dispatch_not_decode()
        => ((SoundBoard.PluginApi.IAudioCodecPlugin)_plugin).SupportedContentTypes.Should().BeEmpty();

    [Fact]
    public void Version_is_resolved_from_assembly_not_empty()
        => _plugin.Version.Should().NotBeNullOrWhiteSpace();

    // ── Argument / lifecycle validation (no network) ──────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateStream_rejects_empty_source(string source)
    {
        var plugin = WithRegistry(Mp3StreamCodec());
        plugin.Invoking(p => p.CreateStream(source))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateStream_without_registry_throws_clear_error()
    {
        // Never Initialized → no registry captured. Must fail before any
        // network call with an actionable message.
        _plugin.Invoking(p => p.CreateStream("http://example.com/stream.mp3"))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*codec registry*");
    }

    [Fact]
    public void CreateStream_with_null_registry_context_throws_clear_error()
    {
        _plugin.Initialize(new FakeContext(registry: null));
        _plugin.Invoking(p => p.CreateStream("http://example.com/stream.mp3"))
            .Should().Throw<InvalidOperationException>();
    }

    // ── End-to-end dispatch via the HttpClient seam ───────────────────

    [Fact]
    public void Seekable_url_hands_codec_a_seekable_transport_with_mime_hint()
    {
        var codec = Mp3StreamCodec();
        var plugin = WithRegistry(codec);

        var handler = new FakeHttpMessageHandler(_ => FakeResponses.SeekableHead(1_000_000, "audio/mpeg"));
        WebStreamCodecPlugin.HttpClientOverride = handler.ToClient();

        using var wave = plugin.CreateStream("http://host/song.mp3");

        codec.ReceivedStream.Should().BeOfType<SeekableHttpStream>();
        codec.ReceivedStream!.CanSeek.Should().BeTrue();
        codec.ReceivedHint.Should().Be("audio/mpeg", "MIME from the probe is preferred as the format hint");
        wave.CanSeek.Should().BeTrue("the codec propagates the transport's CanSeek");
    }

    [Fact]
    public void Live_url_hands_codec_a_nonseekable_transport()
    {
        var codec = Mp3StreamCodec();
        var plugin = WithRegistry(codec);

        // HEAD says no Range; GET (opened by the live transport) returns a body.
        var handler = new FakeHttpMessageHandler(req =>
            req.Method == HttpMethod.Head
                ? FakeResponses.LiveHead("audio/mpeg")
                : FakeResponses.LiveBody(new byte[4096]));
        WebStreamCodecPlugin.HttpClientOverride = handler.ToClient();

        using var wave = plugin.CreateStream("http://host/radio");

        codec.ReceivedStream.Should().BeOfType<LiveTransportStream>();
        codec.ReceivedStream!.CanSeek.Should().BeFalse();
        wave.CanSeek.Should().BeFalse();
    }

    [Fact]
    public void Probe_runs_a_HEAD_before_anything_else()
    {
        var plugin = WithRegistry(Mp3StreamCodec());
        var handler = new FakeHttpMessageHandler(_ => FakeResponses.SeekableHead(1000, "audio/mpeg"));
        WebStreamCodecPlugin.HttpClientOverride = handler.ToClient();

        using var _ = plugin.CreateStream("http://host/song.mp3");

        handler.Requests[0].Method.Should().Be(HttpMethod.Head);
    }

    [Fact]
    public void No_matching_codec_throws_NotSupported_pointing_at_catalog()
    {
        // Registry has only an OGG codec; URL is audio/mpeg.
        var ogg = new FakeCodec("codec.ogg", new[] { ".ogg" }, new[] { "audio/ogg" }, supportsStream: true);
        var plugin = WithRegistry(ogg);
        var handler = new FakeHttpMessageHandler(_ => FakeResponses.SeekableHead(1000, "audio/mpeg"));
        WebStreamCodecPlugin.HttpClientOverride = handler.ToClient();

        plugin.Invoking(p => p.CreateStream("http://host/song.mp3"))
            .Should().Throw<NotSupportedException>()
            .WithMessage("*PLUGINS.md*");
    }

    [Fact]
    public void Codec_throw_disposes_the_transport()
    {
        var codec = Mp3StreamCodec();
        codec.ThrowOnCreate = true;
        var plugin = WithRegistry(codec);
        var handler = new FakeHttpMessageHandler(_ => FakeResponses.SeekableHead(1000, "audio/mpeg"));
        WebStreamCodecPlugin.HttpClientOverride = handler.ToClient();

        plugin.Invoking(p => p.CreateStream("http://host/song.mp3"))
            .Should().Throw<InvalidDataException>();

        // The transport we handed the codec must have been disposed so the
        // HTTP connection doesn't leak. SeekableHttpStream's Read throws
        // ObjectDisposedException once disposed.
        codec.ReceivedStream.Should().NotBeNull();
        codec.ReceivedStream!.Invoking(s => s.ReadByte())
            .Should().Throw<ObjectDisposedException>();
    }

    // ── YouTube resolution ────────────────────────────────────────────

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("HTTPS://WWW.YOUTUBE.COM/WATCH?V=ABC")]    // case-insensitive
    public void IsYouTubeUrl_recognises_canonical_forms(string url)
    {
        YouTubeResolver.IsYouTubeUrl(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://ice1.somafm.com/groovesalad-128-mp3")]
    [InlineData("https://example.com/podcast.mp3")]
    [InlineData("https://notyoutube.com/watch?v=x")]       // similar shape but not YouTube
    [InlineData("")]
    public void IsYouTubeUrl_rejects_other_urls(string url)
    {
        YouTubeResolver.IsYouTubeUrl(url).Should().BeFalse();
    }

    [Fact]
    public void YouTube_url_is_resolved_before_probe_and_dispatch()
    {
        // The whole point of the resolver: HEAD probe + dispatch see the
        // RESOLVED CDN URL, never the youtube.com page URL.
        const string pageUrl    = "https://www.youtube.com/watch?v=test";
        const string resolvedUrl = "https://cdn.example.com/audio.webm";

        var webm = new FakeCodec("codec.webm", new[] { ".webm" }, new[] { "audio/webm" }, supportsStream: true);
        var plugin = WithRegistry(webm);

        WebStreamCodecPlugin.YouTubeResolverOverride = (_, url) =>
        {
            url.Should().Be(pageUrl, "resolver must be handed the original page URL");
            return resolvedUrl;
        };

        var handler = new FakeHttpMessageHandler(req =>
        {
            // Every HTTP call from the plugin must use the resolved URL.
            req.RequestUri!.ToString().Should().Be(resolvedUrl);
            return FakeResponses.SeekableHead(1_000_000, "audio/webm");
        });
        WebStreamCodecPlugin.HttpClientOverride = handler.ToClient();

        using var wave = plugin.CreateStream(pageUrl);

        webm.ReceivedStream.Should().BeOfType<SeekableHttpStream>();
        webm.ReceivedHint.Should().Be("audio/webm");
    }

    [Fact]
    public void Non_youtube_urls_skip_the_resolver_entirely()
    {
        var mp3 = Mp3StreamCodec();
        var plugin = WithRegistry(mp3);

        // If the resolver runs for a non-YouTube URL, this throws and the
        // test fails. It must not run.
        WebStreamCodecPlugin.YouTubeResolverOverride =
            (_, _) => throw new InvalidOperationException("resolver should not have been called");

        var handler = new FakeHttpMessageHandler(_ => FakeResponses.SeekableHead(1000, "audio/mpeg"));
        WebStreamCodecPlugin.HttpClientOverride = handler.ToClient();

        using var _wave = plugin.CreateStream("https://example.com/podcast.mp3");
        mp3.ReceivedStream.Should().BeOfType<SeekableHttpStream>();
    }

    [Fact]
    public void Resolver_failure_surfaces_as_InvalidOperationException()
    {
        var plugin = WithRegistry(Mp3StreamCodec());
        WebStreamCodecPlugin.YouTubeResolverOverride =
            (_, url) => throw new InvalidOperationException($"resolve failed for {url}");
        // HttpClientOverride not strictly needed (we throw before any HTTP),
        // but set it to a tripwire just in case the resolver branch were
        // accidentally skipped.
        WebStreamCodecPlugin.HttpClientOverride =
            new FakeHttpMessageHandler(_ => throw new InvalidOperationException("HTTP must not be called")).ToClient();

        plugin.Invoking(p => p.CreateStream("https://youtu.be/abc"))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*resolve failed*");
    }
}
