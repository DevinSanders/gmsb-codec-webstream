using System;
using System.Net.Http;
using System.Threading;

namespace WebStreamCodecPlugin;

/// <summary>
/// Single-shot HEAD probe to decide whether a URL is seekable. The host's
/// <c>CanSeek</c> contract requires us to know <i>before</i> opening the
/// stream — once playback starts, switching the UI between scrub-bar and
/// "● LIVE" badge would be jarring.
///
/// <para>The decision is intentionally conservative: only mark seekable
/// when the server explicitly advertises both <c>Accept-Ranges: bytes</c>
/// and a known <c>Content-Length</c>. Anything ambiguous (Shoutcast's
/// non-HTTP <c>ICY 200 OK</c> response line, missing length, no Accept-Ranges)
/// falls through to the ICY reader.</para>
/// </summary>
internal static class HttpProbe
{
    public readonly record struct Result(bool LooksSeekable, long? ContentLength, string? ContentType);

    public static Result Run(HttpClient http, string url)
    {
        // Short timeout — we don't want a slow probe to block the UI for
        // long. If the probe fails entirely (network error, Shoutcast's
        // non-HTTP response line that confuses HttpClient, etc.), we
        // return LooksSeekable=false and let the caller fall back to ICY.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            // Some servers serve different metadata for HEAD vs GET; this
            // is the best we can do without burning bandwidth on a GET.
            using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!response.IsSuccessStatusCode)
                return new Result(false, null, null);

            var acceptsRanges = false;
            foreach (var v in response.Headers.AcceptRanges)
            {
                if (string.Equals(v, "bytes", StringComparison.OrdinalIgnoreCase))
                {
                    acceptsRanges = true;
                    break;
                }
            }

            var length = response.Content.Headers.ContentLength;
            var contentType = response.Content.Headers.ContentType?.MediaType;

            // Seekable iff the server explicitly says so AND knows the size.
            // A range-supporting server with unknown length is still odd
            // enough that we prefer the live-stream path.
            return new Result(acceptsRanges && length.HasValue && length.Value > 0, length, contentType);
        }
        catch
        {
            // Probe failed (timeout, ICY non-HTTP response, DNS error, etc.).
            // Live-stream path will retry with a real GET — if that also
            // fails, the user gets an exception they can act on.
            return new Result(false, null, null);
        }
    }
}
