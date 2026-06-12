using System;
using FluentAssertions;
using Xunit;

namespace WebStreamCodecPlugin.Tests;

/// <summary>Pure tests for the codec-selection logic — no network, no probe.
/// Exercises <see cref="WebStreamCodecPlugin.ResolveCodec"/> and friends
/// directly against a fake registry.</summary>
public class DispatchTests
{
    private static FakeCodec Mp3() =>
        new("codec.mp3", new[] { ".mp3" }, new[] { "audio/mpeg", "audio/mp3" }, supportsStream: true);

    private static FakeCodec Ogg() =>
        new("codec.ogg", new[] { ".ogg" }, new[] { "audio/ogg" }, supportsStream: true);

    [Fact]
    public void Resolves_by_content_type_first()
    {
        var mp3 = Mp3();
        var registry = new FakeRegistry(Ogg(), mp3);

        WebStreamCodecPlugin.ResolveCodec(registry, "http://h/track", "audio/mpeg")
            .Should().BeSameAs(mp3);
    }

    [Fact]
    public void Content_type_match_ignores_charset_parameter()
    {
        var mp3 = Mp3();
        var registry = new FakeRegistry(mp3);

        WebStreamCodecPlugin.ResolveCodec(registry, "http://h/track", "audio/mpeg; charset=utf-8")
            .Should().BeSameAs(mp3);
    }

    [Fact]
    public void Falls_back_to_url_extension_when_no_content_type()
    {
        var mp3 = Mp3();
        var registry = new FakeRegistry(Ogg(), mp3);

        WebStreamCodecPlugin.ResolveCodec(registry, "http://h/song.mp3", contentType: null)
            .Should().BeSameAs(mp3);
    }

    [Fact]
    public void Falls_back_to_extension_when_content_type_unmatched()
    {
        var mp3 = Mp3();
        // Server lied / sent a generic type, but the URL ends in .mp3.
        var registry = new FakeRegistry(mp3);

        WebStreamCodecPlugin.ResolveCodec(registry, "http://h/song.mp3", "application/octet-stream")
            .Should().BeSameAs(mp3);
    }

    [Fact]
    public void Skips_codecs_that_do_not_support_stream_input()
    {
        // A non-stream MP3 codec claims audio/mpeg but can't take a Stream.
        // ResolveCodec must skip it (returning it would only throw at
        // dispatch time) — with nothing else installed, that means no
        // stream-capable codec is found and ResolveCodec throws.
        var fileOnlyMp3 = new FakeCodec("codec.mp3.fileonly", new[] { ".mp3" }, new[] { "audio/mpeg" }, supportsStream: false);
        var registry = new FakeRegistry(fileOnlyMp3);

        FluentActions.Invoking(() => WebStreamCodecPlugin.ResolveCodec(registry, "http://h/song.mp3", "audio/mpeg"))
            .Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Prefers_stream_capable_codec_over_a_file_only_one_for_same_mime()
    {
        var fileOnlyMp3 = new FakeCodec("codec.mp3.fileonly", new[] { ".mp3" }, new[] { "audio/mpeg" }, supportsStream: false);
        var streamMp3 = Mp3();
        // File-only one is listed first, but ResolveCodec must walk past it.
        var registry = new FakeRegistry(fileOnlyMp3, streamMp3);

        WebStreamCodecPlugin.ResolveCodec(registry, "http://h/song.mp3", "audio/mpeg")
            .Should().BeSameAs(streamMp3);
    }

    [Fact]
    public void No_codec_throws_NotSupported_naming_the_catalog()
    {
        var registry = new FakeRegistry(Ogg());

        FluentActions.Invoking(() => WebStreamCodecPlugin.ResolveCodec(registry, "http://h/song.aac", "audio/aac"))
            .Should().Throw<NotSupportedException>()
            .WithMessage("*PLUGINS.md*");
    }

    [Fact]
    public void Error_names_a_matching_file_only_codec_to_guide_the_user()
    {
        var fileOnlyMp3 = new FakeCodec("codec.mp3.fileonly", new[] { ".mp3" }, new[] { "audio/mpeg" }, supportsStream: false);
        var registry = new FakeRegistry(fileOnlyMp3);

        FluentActions.Invoking(() => WebStreamCodecPlugin.ResolveCodec(registry, "http://h/song.mp3", "audio/mpeg"))
            .Should().Throw<NotSupportedException>()
            .WithMessage("*codec.mp3.fileonly*");
    }

    [Theory]
    [InlineData("http://h/song.mp3", ".mp3")]
    [InlineData("https://h/path/to/track.OGG", ".OGG")]
    [InlineData("http://h/stream", "")]
    [InlineData("http://h/radio?fmt=mp3", "")]   // query string isn't an extension
    [InlineData("not a url", "")]
    public void ExtensionFromUrl_extracts_path_extension(string url, string expected)
    {
        WebStreamCodecPlugin.ExtensionFromUrl(url).Should().Be(expected);
    }
}
