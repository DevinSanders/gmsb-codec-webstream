using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using SoundBoard.PluginApi;

namespace WebStreamCodecPlugin.Tests;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a per-request
/// callback instead of touching the network. Lets the probe + transports
/// be exercised deterministically.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => _responder = responder;

    /// <summary>Record of every request received, in order. Handy for
    /// asserting that (e.g.) a HEAD probe ran before a Range GET.</summary>
    public List<(HttpMethod Method, string Url, string? Range)> Requests { get; } = new();

    // Both overloads are exercised: HttpProbe.Run and SeekableHttpStream use
    // the synchronous HttpClient.Send; IcyAudioStream (live path) uses the
    // async SendAsync. The base handler's sync Send throws by default, so
    // we MUST override both or the probe silently fails into the live path.
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => Respond(request);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(Respond(request));

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Range?.ToString()));
        var response = _responder(request);
        response.RequestMessage = request;
        return response;
    }

    /// <summary>Convenience: wrap this handler in an HttpClient.</summary>
    public HttpClient ToClient() => new(this) { Timeout = Timeout.InfiniteTimeSpan };
}

/// <summary>Builders for the canned <see cref="HttpResponseMessage"/>s the
/// tests hand back from <see cref="FakeHttpMessageHandler"/>.</summary>
internal static class FakeResponses
{
    /// <summary>HEAD response advertising Range support + a known length →
    /// drives the seekable path.</summary>
    public static HttpResponseMessage SeekableHead(long length, string? contentType)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        r.Headers.AcceptRanges.Add("bytes");
        r.Content.Headers.ContentLength = length;
        if (contentType is not null)
            r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return r;
    }

    /// <summary>HEAD response with no Range support → drives the live path.</summary>
    public static HttpResponseMessage LiveHead(string? contentType)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        if (contentType is not null)
            r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return r;
    }

    /// <summary>206 Partial Content carrying the requested byte range out of
    /// <paramref name="full"/>. Honours the request's Range header.</summary>
    public static HttpResponseMessage PartialContent(byte[] full, HttpRequestMessage request)
    {
        var range = request.Headers.Range!.Ranges.Single();
        long from = range.From!.Value;
        long to = Math.Min(range.To!.Value, full.Length - 1);
        int len = (int)(to - from + 1);
        var slice = new byte[len];
        Array.Copy(full, from, slice, 0, len);

        var r = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice),
        };
        r.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, full.Length);
        return r;
    }

    /// <summary>200 OK streaming a body, optionally with ICY metadata
    /// interleaving (icy-metaint header + inline metadata blocks).</summary>
    public static HttpResponseMessage LiveBody(byte[] body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    }
}

/// <summary>A codec stand-in. Records what it was handed and can be told to
/// throw, so the transport-ownership-on-failure path is testable.</summary>
internal sealed class FakeCodec : IAudioCodecPlugin
{
    public FakeCodec(string id, IEnumerable<string> patterns, IEnumerable<string> contentTypes, bool supportsStream)
    {
        Id = id;
        SupportedPatterns = patterns.ToArray();
        SupportedContentTypes = contentTypes.ToArray();
        SupportsStreamInput = supportsStream;
    }

    public string Id { get; }
    public string Name => Id;
    public string Version => "1.0.0";
    public string Author => "test";
    public string Description => "fake";

    public IEnumerable<string> SupportedPatterns { get; }
    public IEnumerable<string> SupportedContentTypes { get; }
    public bool SupportsStreamInput { get; }

    /// <summary>When true, <see cref="CreateStream(Stream, string)"/> throws
    /// after capturing the transport — exercising the dispose-on-failure path.</summary>
    public bool ThrowOnCreate { get; set; }

    /// <summary>The Stream the plugin handed us (captured before any throw).</summary>
    public Stream? ReceivedStream { get; private set; }
    public string? ReceivedHint { get; private set; }

    public void Initialize(IPluginContext context) { }
    public void Shutdown() { }

    public WaveStream CreateStream(string source)
        => throw new NotSupportedException("FakeCodec only supports Stream input in these tests.");

    public WaveStream CreateStream(Stream source, string formatHint)
    {
        ReceivedStream = source;
        ReceivedHint = formatHint;
        if (ThrowOnCreate) throw new InvalidDataException("simulated decode failure");
        return new PassthroughWaveStream(source);
    }
}

/// <summary>Minimal <see cref="WaveStream"/> that owns the transport Stream
/// and disposes it (per the SDK ownership contract) so ownership-transfer
/// can be asserted. <see cref="CanSeek"/> follows the input.</summary>
internal sealed class PassthroughWaveStream : WaveStream
{
    private readonly Stream _source;
    public PassthroughWaveStream(Stream source) => _source = source;

    public override WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public override long Length => _source.CanSeek ? _source.Length : 0;
    public override bool CanSeek => _source.CanSeek;
    public override long Position
    {
        get => _source.CanSeek ? _source.Position : 0;
        set { if (_source.CanSeek) _source.Position = value; }
    }
    public override int Read(byte[] buffer, int offset, int count) => _source.Read(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>In-memory <see cref="IAudioCodecRegistry"/> over a fixed codec list.
/// GetBy* mirror the host's "first match wins" semantics.</summary>
internal sealed class FakeRegistry : IAudioCodecRegistry
{
    private readonly List<IAudioCodecPlugin> _codecs;
    public FakeRegistry(params IAudioCodecPlugin[] codecs) => _codecs = codecs.ToList();

    public IEnumerable<IAudioCodecPlugin> All => _codecs;

    public IAudioCodecPlugin? GetByExtension(string extension)
        => _codecs.FirstOrDefault(c => c.SupportedPatterns.Any(
            p => string.Equals(p, extension, StringComparison.OrdinalIgnoreCase)));

    public IAudioCodecPlugin? GetByContentType(string mimeType)
        => _codecs.FirstOrDefault(c => c.SupportedContentTypes.Any(
            m => string.Equals(m, mimeType, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>Hands a registry to the plugin at Initialize.</summary>
internal sealed class FakeContext : IPluginContext
{
    public FakeContext(IAudioCodecRegistry? registry) => CodecRegistry = registry;
    public IWindowService? WindowService => null;
    public string PluginDataPath => Path.GetTempPath();
    public IAudioCodecRegistry? CodecRegistry { get; }
    public ISidechainRegistry? Sidechain => null;
}

/// <summary>MemoryStream that records disposal — for asserting transport
/// ownership transfer.</summary>
internal sealed class DisposeTrackingStream : MemoryStream
{
    public bool Disposed { get; private set; }
    public DisposeTrackingStream(byte[] buffer) : base(buffer, writable: false) { }
    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
