# gmsb-codec-webstream

Cross-platform URL-based audio transport plugin for
[Game Master Sound Board](https://github.com/DevinSanders/game-master-soundboard).
Adds playback of any HTTP/HTTPS audio URL: Icecast / Shoutcast internet
radio, podcast-style progressive downloads, static MP3/OGG/FLAC hosting,
anything reachable over HTTP.

**Transport-only.** This plugin opens the HTTP body and detects seekability,
then hands the bytes off to whichever format-specific codec plugin you
have installed (`gmsb-codec-mp3`, `gmsb-codec-ogg`, `gmsb-codec-flac`, …)
via the host's `IAudioCodecRegistry`. No bundled decoder — runs on
Windows, macOS, and Linux with zero native dependencies.

## Install

**Paid plugin.** The source is open here for reference, but the pre-built
binary is distributed pay-what-you-want on itch.io:

**→ https://dsand64.itch.io/gmsb-codec-webstream**

Download the `.zip` from that page and drop it onto **Settings → Plugin
Manager** in Game Master Sound Board. Restart when prompted, then enable it under **Settings → Plugins**.

## Seekability detection

Every URL gets a HEAD probe. The host's seek bar / loop toggle is gated
on `ISeekableSampleProvider.IsSeekable`, so we drive that off what the
server actually advertises:

| Server response                                     | Transport                | UI                       |
|-----------------------------------------------------|--------------------------|--------------------------|
| `Accept-Ranges: bytes` + known `Content-Length`     | `SeekableHttpStream`     | Scrub slider + loop      |
| Icecast / Shoutcast (ICY metadata, no Range)        | `LiveTransportStream`    | "● LIVE" badge, no loop  |
| HEAD failed or no Range support                     | `LiveTransportStream`    | "● LIVE" badge, no loop  |

The codec propagates the transport's `CanSeek` to its returned
`WaveStream` (the first-party `gmsb-codec-mp3` / `gmsb-codec-ogg` /
`gmsb-codec-flac` all do this), so the host UI follows automatically.

## Format support is what you install

Install `gmsb-codec-mp3` → MP3 URLs work. Install `gmsb-codec-ogg` →
OGG URLs work. Install `gmsb-codec-flac` → FLAC URLs work. URLs whose
`Content-Type` has no matching Stream-capable codec installed fail with
an error pointing at the catalog.

## Manifest

| Field     | Value                       |
|-----------|-----------------------------|
| publisher | `github.DevinSanders`       |
| id        | `codec.webstream`           |
| version   | `1.0.0`                     |
| entryDll  | `WebStreamCodecPlugin.dll`  |

## License

Released under the [MIT License](LICENSE).

Third-party components used by this plugin:

- No decoder dependencies — this is a transport-only plugin that dispatches HTTP bytes to whichever format-specific codec plugin is installed.