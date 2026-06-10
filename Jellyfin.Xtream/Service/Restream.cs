// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A live stream implementation that can be restreamed.
/// </summary>
public class Restream : ILiveStream, IDirectStreamProvider, IDisposable
{
    /// <summary>
    /// The global constant for the restream tuner host.
    /// </summary>
    public const string TunerHost = "Xtream-Restream";

    /// <summary>The size of a single MPEG-TS packet.</summary>
    private const int TsPacketSize = 188;

    /// <summary>The 33-bit MPEG-TS PCR base wraps at this modulus (~26.5 h @ 90 kHz).</summary>
    private const long PcrModulus = 1L << 33;

    /// <summary>
    /// A backward PCR step larger than this is a genuine source discontinuity, not reconnect
    /// overlap — the minute-truncated timeshift restart can only overlap by ≤60 s (+ lead/backoff).
    /// </summary>
    private const long DiscontinuityGuardPcrTicks = 10L * 60 * 90000; // 10 min @ 90 kHz

    /// <summary>
    /// Release the overlap trim if no PCR at all was seen within this many trimmed bytes: a real TS
    /// carries a PCR at least every 100 ms, so a PCR-less feed is identified almost immediately and
    /// passed through untrimmed (there is nothing to gate on).
    /// </summary>
    private const long TrimNoPcrProbeBytes = 2L * 1024 * 1024;

    /// <summary>
    /// Give up trimming once the trimmed span itself covers more than this much PCR time — the
    /// plausible reconnect overlap is ≤~67 s, so a longer span means the overlap model is wrong and
    /// passing data through beats eating live content. Time-based on purpose: a byte cap is wrong
    /// for high-bitrate channels (60 s of 12 Mbps is ~90 MB).
    /// </summary>
    private const long TrimWindowPcrTicks = 90L * 90000; // 90 s @ 90 kHz

    /// <summary>A reconnect that delivered (post-trim) less than this re-served only old content — it was barren.</summary>
    private const long BarrenThresholdBytes = TsPacketSize * 1000L; // ~188 KB ≈ 0.2 s

    /// <summary>Cap on the forward skip escalation applied after consecutive barren reconnects.</summary>
    private const int MaxBarrenSkipMinutes = 5;

    /// <summary>Give up TS alignment past this many sync-less bytes and degrade to a raw copy.</summary>
    private const long RawPassthroughThresholdBytes = 1024 * 1024;

    /// <summary>Cap on the accumulated skip baseline, bounding total drift from the Caledonian alignment per session.</summary>
    private static readonly TimeSpan MaxSkipBaseline = TimeSpan.FromMinutes(15);

    /// <summary>How far the media clock may run ahead of wall-clock before pacing kicks in (start-up buffer).</summary>
    private static readonly TimeSpan PacingLead = TimeSpan.FromSeconds(5);

    private static readonly HttpStatusCode[] _redirects = [
        HttpStatusCode.Moved,
        HttpStatusCode.MovedPermanently,
        HttpStatusCode.PermanentRedirect,
        HttpStatusCode.Redirect,
    ];

    private readonly WrappedBufferStream _buffer;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _tokenSource;
    private readonly string _url;
    private readonly Func<TimeSpan, string>? _urlProvider;
    private readonly bool _pace;

    private Task? _copyTask;
    private Stream? _inputStream;

    // Reconnect-overlap state. The timeshift restart is minute-granular, so every reconnect can
    // re-serve up to ~60 s of content already written to the buffer; written again, it reaches the
    // demuxer as a timestamp rewind that freezes video and replays audio. We track the last media
    // clock actually delivered and trim reconnected input until it passes that point; reconnects
    // that deliver nothing new (stuck on the same provider chunk) escalate a forward skip instead
    // of looping forever on a frozen playlist, and a skip that finally delivers is folded into
    // _skipBaseline so later reconnects don't re-request (and re-trim) the skipped span.
    //
    // The clock is the video PES DTS (PTS fallback), NOT the PCR: these provider feeds carry no
    // usable PCR (the PCR pacing path already falls back to the byte ceiling for the same reason),
    // and a PCR-gated trim simply never arms. Video PES headers exist on every frame and the DTS
    // is monotone per stream, which is exactly what the gate needs. Same 90 kHz units as PCR.
    private long _lastDeliveredClock = -1;
    private bool _trimOverlap;
    private long _trimmedBytes;
    private long _trimFirstClock = -1;
    private bool _trimSawClock;
    private int _barrenReconnects;
    private TimeSpan _skipBaseline = TimeSpan.Zero;
    private long _deliveredThisConnection;

    /// <summary>
    /// Initializes a new instance of the <see cref="Restream"/> class.
    /// </summary>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="mediaSource">The media which must be restreamed.</param>
    /// <param name="urlProvider">
    /// Optional factory returning a fresh upstream URL whenever the restream reconnects after an
    /// upstream EOF (live/catch-up only). For time-shifted catch-up channels this re-aligns the
    /// <c>start</c> to "now", which — the offset being constant — resumes seamlessly. The
    /// <see cref="TimeSpan"/> argument is a forward skip to add to that start: it stays zero in
    /// normal operation and grows by a minute per barren reconnect (a restart that re-served only
    /// already-delivered content), so a dead provider chunk is skipped instead of looped on. When
    /// null the original <see cref="MediaSourceInfo.Path"/> is reused on every reconnect.
    /// </param>
    /// <param name="pace">
    /// When true, the upstream is throttled to ~1x real time using the MPEG-TS PCR clock. Required for
    /// catch-up/timeshift feeds, which the provider delivers as a fast download rather than a paced live
    /// stream — without pacing the consumer races to the end and the stream stops after a few seconds.
    /// </param>
    public Restream(IServerApplicationHost appHost, IHttpClientFactory httpClientFactory, ILogger logger, MediaSourceInfo mediaSource, Func<TimeSpan, string>? urlProvider = null, bool pace = false)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _urlProvider = urlProvider;
        _pace = pace;
        MediaSource = mediaSource;

        _buffer = new WrappedBufferStream(32 * 1024 * 1024); // 32MiB — headroom for FFmpeg stalls on discontinuities
        _tokenSource = new CancellationTokenSource();

        OriginalStreamId = MediaSource.Id;
        UniqueId = Guid.NewGuid().ToString();

        _url = MediaSource.Path;
        string path = $"/LiveTv/LiveStreamFiles/{UniqueId}/stream.ts";
        MediaSource.Path = appHost.GetSmartApiUrl(IPAddress.Any) + path;
        MediaSource.EncoderPath = appHost.GetApiUrlForLocalAccess() + path;
        MediaSource.Protocol = MediaProtocol.Http;
    }

    /// <inheritdoc />
    public int ConsumerCount { get; set; }

    /// <inheritdoc />
    public string OriginalStreamId { get; set; }

    /// <inheritdoc />
    public string TunerHostId => TunerHost;

    /// <inheritdoc />
    public bool EnableStreamSharing => true;

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public string UniqueId { get; init; }

    /// <inheritdoc />
    public async Task Open(CancellationToken openCancellationToken)
    {
        if (_copyTask != null)
        {
            _logger.LogWarning("Restream for channel {ChannelId} is already open.", MediaSource.Id);
            return;
        }

        // pace=true → catch-up/timeshift feed (throttled to 1x); pace=false → plain live. Logged
        // because the provider's tv_archive flag flaps and silently flips channels between the two
        // paths — without this line, "zero trim logs" is ambiguous between a broken trim and an
        // unpaced channel (which cost us a full debugging round in 0.9.7.0/0.9.7.1).
        _logger.LogInformation("Starting restream for channel {ChannelId} (pace={Pace}).", MediaSource.Id, _pace);

        // Await the first connection so the buffer starts filling before Open returns; later
        // upstream EOFs are handled in the background by ConnectAndPump's continuation.
        await ConnectAndPumpAsync(openCancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Connects to the (possibly re-aligned) upstream URL and starts copying it into the shared buffer.
    /// When the upstream ends, the continuation reconnects automatically for live/catch-up streams so
    /// the consumer never sees an EOF; non-infinite streams (VOD/series) end normally.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task ConnectAndPumpAsync(CancellationToken cancellationToken)
    {
        string channelId = MediaSource.Id;
        _deliveredThisConnection = 0;
        _trimmedBytes = 0;
        _trimFirstClock = -1;
        _trimSawClock = false;
        string url = _urlProvider?.Invoke(_skipBaseline + TimeSpan.FromMinutes(_barrenReconnects)) ?? _url;

        // Response stream is disposed manually.
        HttpResponseMessage response = await _httpClientFactory.CreateClient(NamedClient.Default)
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(true);
        _logger.LogDebug("Stream for channel {ChannelId} using url {Url}", channelId, url);

        // Handle a manual redirect in the case of a HTTPS to HTTP downgrade.
        if (_redirects.Contains(response.StatusCode))
        {
            _logger.LogDebug("Stream for channel {ChannelId} redirected to url {Url}", channelId, response.Headers.Location);
            response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .GetAsync(response.Headers.Location, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(true);
        }

        Stream input = await response.Content.ReadAsStreamAsync(CancellationToken.None).ConfigureAwait(false);
        _inputStream = input;

        // Both paced (catch-up) and plain live go through the same packet-scanning loop: field data
        // showed the provider re-serves 12-29 s of backlog on PLAIN live reconnects too (every
        // ~10-26 s), so the overlap trim must run on both paths. Pacing itself stays catch-up-only.
        Task pump = CopyLoopAsync(input, _buffer, _tokenSource.Token);
        _copyTask = pump.ContinueWith(
                OnUpstreamEnded,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
    }

    /// <summary>
    /// Copies the MPEG-TS upstream into the buffer, scanning packets for the trim/clock machinery on
    /// every path, and — for catch-up/timeshift feeds only (<c>_pace</c>) — throttling to ~1x real
    /// time using the stream's PCR clock. Catch-up feeds are delivered as a fast download; without
    /// pacing the consumer (FFmpeg) races to the end and the "live" stream stops after seconds, and
    /// the ring buffer overruns ("Reader cannot keep up"). Plain live feeds are already paced by the
    /// provider, but still need the packet scan: their reconnects re-serve backlog that must be
    /// trimmed. A stream that turns out not to be MPEG-TS falls back to raw passthrough.
    /// </summary>
    /// <param name="input">The upstream content stream.</param>
    /// <param name="output">The shared restream buffer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task CopyLoopAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        // Anti-race ceiling: never deliver faster than ~1.25x the declared bitrate. This alone keeps the
        // stream from racing even when the PCR clock is unusable (some catch-up feeds have no/garbled PCR),
        // so the 24h window can't be drained in minutes.
        long bitrateBits = MediaSource.Bitrate > 0 ? MediaSource.Bitrate.Value : 8_000_000;
        double targetBytesPerSecond = (bitrateBits / 8.0) * 1.25;
        double leadSeconds = PacingLead.TotalSeconds;

        byte[] buf = new byte[TsPacketSize * 512]; // ~94 KiB, whole TS packets
        int leftover = 0;
        bool aligned = false;
        bool rawPassthrough = false;
        long unalignedScanned = 0;

        // Fine 1x pacing from the PCR clock when present: accumulate only "clean" PCR deltas (catch-up
        // streams splice timelines, so a backward/huge jump is ignored rather than re-based in a loop).
        long lastPcr = -1;
        double mediaElapsedSeconds = 0;
        const long MaxPcrDelta = 10 * 90000; // 10s in 90 kHz units; bigger = treat as a discontinuity
        long bytesDelivered = 0;
        Stopwatch clock = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested)
        {
            int read = await input.ReadAsync(buf.AsMemory(leftover, buf.Length - leftover), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break; // upstream EOF
            }

            int available = leftover + read;

            // Parachute for a stream that is not MPEG-TS at all: without it the aligner below would
            // stash-and-rescan forever and never write a byte. Restream inputs are TS in practice,
            // so this only exists to degrade into a dumb copy instead of a silent stall.
            if (rawPassthrough)
            {
                await output.WriteAsync(buf.AsMemory(0, available), cancellationToken).ConfigureAwait(false);
                bytesDelivered += available;
                _deliveredThisConnection += available;
                leftover = 0;
                continue;
            }

            int start = 0;

            // Align to the first TS sync byte once (real streams start aligned, so this is usually a no-op).
            if (!aligned)
            {
                int sync = FindSync(buf, available);
                if (sync < 0)
                {
                    unalignedScanned += available;
                    if (unalignedScanned > RawPassthroughThresholdBytes)
                    {
                        _logger.LogWarning(
                            "Restream for channel {ChannelId} found no TS sync in {Bytes} bytes; falling back to raw passthrough (no trim/pacing).",
                            MediaSource.Id,
                            unalignedScanned);
                        rawPassthrough = true;
                        _trimOverlap = false;
                        await output.WriteAsync(buf.AsMemory(0, available), cancellationToken).ConfigureAwait(false);
                        bytesDelivered += available;
                        _deliveredThisConnection += available;
                        leftover = 0;
                        continue;
                    }

                    leftover = Math.Min(available, TsPacketSize * 2);
                    Array.Copy(buf, available - leftover, buf, 0, leftover);
                    continue;
                }

                start = sync;
                aligned = true;
            }

            int packets = (available - start) / TsPacketSize;
            int end = start + (packets * TsPacketSize);

            // Post-reconnect overlap trim: drop packets until the PCR passes the last delivered one,
            // so the re-served span never reaches the demuxer as a timestamp rewind. All comparisons
            // are modular in 33-bit PCR space so a wrap mid-overlap doesn't defeat the gate. Escape
            // hatches: a backward step too large to be overlap is a genuine source discontinuity
            // (pass through); a trimmed span longer than any plausible overlap means the model is
            // wrong (pass through); a feed with no PCR at all cannot be gated (pass through).
            int writeFrom = start;
            if (_trimOverlap)
            {
                writeFrom = end;
                for (int i = start; i < end; i += TsPacketSize)
                {
                    long mediaClock = TryReadVideoClock(buf, i);
                    bool release = false;
                    if (mediaClock >= 0)
                    {
                        _trimSawClock = true;
                        if (_trimFirstClock < 0)
                        {
                            _trimFirstClock = mediaClock;
                        }

                        long aheadOfDelivered = PcrDistance(_lastDeliveredClock, mediaClock);
                        release = aheadOfDelivered > 0 || -aheadOfDelivered > DiscontinuityGuardPcrTicks
                            || PcrDistance(_trimFirstClock, mediaClock) > TrimWindowPcrTicks;
                    }
                    else if (!_trimSawClock && _trimmedBytes + (i - start) >= TrimNoPcrProbeBytes)
                    {
                        release = true;
                    }

                    if (release)
                    {
                        writeFrom = i;
                        _trimOverlap = false;
                        _logger.LogInformation(
                            "Restream for channel {ChannelId} trimmed {Bytes} bytes of reconnect overlap.",
                            MediaSource.Id,
                            _trimmedBytes + (i - start));
                        break;
                    }
                }

                _trimmedBytes += (_trimOverlap ? end : writeFrom) - start;
            }

            // Over the delivered span: accumulate clean PCR deltas for pacing (no sleeping here;
            // pacing is applied per chunk below), and remember the last delivered video PES clock
            // for reconnect-overlap trimming (these feeds often carry no usable PCR).
            for (int i = writeFrom; i < end; i += TsPacketSize)
            {
                long deliveredClock = TryReadVideoClock(buf, i);
                if (deliveredClock >= 0)
                {
                    _lastDeliveredClock = deliveredClock;
                }

                long pcr = TryReadPcr(buf, i);
                if (pcr < 0)
                {
                    continue;
                }

                if (lastPcr >= 0)
                {
                    long delta = pcr - lastPcr;
                    if (delta > 0 && delta <= MaxPcrDelta)
                    {
                        mediaElapsedSeconds += delta / 90000.0;
                    }
                }

                lastPcr = pcr;
            }

            if (end > writeFrom)
            {
                await output.WriteAsync(buf.AsMemory(writeFrom, end - writeFrom), cancellationToken).ConfigureAwait(false);
                bytesDelivered += end - writeFrom;
                _deliveredThisConnection += end - writeFrom;
            }

            leftover = available - end;
            if (leftover > 0)
            {
                Array.Copy(buf, end, buf, 0, leftover);
            }

            // Pace (catch-up/timeshift only): sleep if we are ahead by the media clock (PCR, exact 1x)
            // OR by the byte ceiling (anti-race fallback). Whichever says we are further ahead wins.
            // Plain live feeds are paced by the provider; throttling them would starve the consumer.
            if (_pace)
            {
                double elapsed = clock.Elapsed.TotalSeconds;
                double pcrAhead = mediaElapsedSeconds > 0
                    ? mediaElapsedSeconds - elapsed - leadSeconds
                    : double.NegativeInfinity;
                double byteAhead = (bytesDelivered / targetBytesPerSecond) - elapsed - leadSeconds;
                double ahead = Math.Max(pcrAhead, byteAhead);
                if (ahead > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(ahead), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Signed shortest distance from one PCR to another in modular 33-bit PCR space: positive when
    /// <paramref name="to"/> is ahead of <paramref name="from"/>, correct across the PCR wrap.
    /// </summary>
    /// <param name="from">The reference PCR (90 kHz units).</param>
    /// <param name="to">The PCR to compare (90 kHz units).</param>
    /// <returns>The signed distance in 90 kHz units.</returns>
    private static long PcrDistance(long from, long to)
    {
        long diff = (to - from) & (PcrModulus - 1);
        if (diff > PcrModulus >> 1)
        {
            diff -= PcrModulus;
        }

        return diff;
    }

    /// <summary>Finds the offset of the first TS packet boundary (three sync bytes 188 apart).</summary>
    /// <param name="b">The buffer.</param>
    /// <param name="length">The number of valid bytes in the buffer.</param>
    /// <returns>The sync offset, or -1 when none is found yet.</returns>
    private static int FindSync(byte[] b, int length)
    {
        for (int i = 0; i + (TsPacketSize * 2) < length; i++)
        {
            if (b[i] == 0x47 && b[i + TsPacketSize] == 0x47 && b[i + (TsPacketSize * 2)] == 0x47)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads the 90 kHz media clock from a video PES header starting in the TS packet at the given
    /// offset: the DTS when present (monotone per stream — ideal gate clock), the PTS otherwise.
    /// Returns -1 when the packet doesn't start a video PES with a timestamp. Used for the
    /// reconnect-overlap trim because these provider feeds carry no usable PCR.
    /// </summary>
    /// <param name="b">The buffer.</param>
    /// <param name="o">The packet offset.</param>
    /// <returns>The DTS/PTS in 90 kHz units, or -1.</returns>
    private static long TryReadVideoClock(byte[] b, int o)
    {
        if (b[o] != 0x47)
        {
            return -1; // not aligned on a sync byte
        }

        if ((b[o + 1] & 0x80) != 0)
        {
            return -1; // transport_error_indicator set
        }

        if ((b[o + 1] & 0x40) == 0)
        {
            return -1; // not a payload_unit_start packet — no PES header here
        }

        int adaptationFieldControl = (b[o + 3] >> 4) & 0x3;
        if (adaptationFieldControl == 0 || adaptationFieldControl == 2)
        {
            return -1; // no payload
        }

        int payload = o + 4;
        if (adaptationFieldControl == 3)
        {
            int adaptationFieldLength = b[o + 4];
            payload += 1 + adaptationFieldLength;
        }

        // Full PES header with both timestamps needs up to 19 bytes.
        if (payload + 19 > o + TsPacketSize)
        {
            return -1;
        }

        if (b[payload] != 0x00 || b[payload + 1] != 0x00 || b[payload + 2] != 0x01)
        {
            return -1; // no PES start code
        }

        int streamId = b[payload + 3];
        if (streamId < 0xE0 || streamId > 0xEF)
        {
            return -1; // not a video elementary stream
        }

        int ptsDtsFlags = (b[payload + 7] >> 6) & 0x3;
        if ((ptsDtsFlags & 0x2) == 0)
        {
            return -1; // no PTS
        }

        // PTS sits at +9; when the DTS is present it follows at +14 — prefer it (monotone).
        int p = ptsDtsFlags == 0x3 ? payload + 14 : payload + 9;
        return (((long)b[p] >> 1) & 0x7) << 30
            | (long)b[p + 1] << 22
            | (((long)b[p + 2] >> 1) & 0x7F) << 15
            | (long)b[p + 3] << 7
            | (((long)b[p + 4] >> 1) & 0x7F);
    }

    /// <summary>Reads the 90 kHz PCR base from a TS packet at the given offset, or -1 when absent.</summary>
    /// <param name="b">The buffer.</param>
    /// <param name="o">The packet offset.</param>
    /// <returns>The PCR base in 90 kHz units, or -1.</returns>
    private static long TryReadPcr(byte[] b, int o)
    {
        if (b[o] != 0x47)
        {
            return -1; // not aligned on a sync byte
        }

        if ((b[o + 1] & 0x80) != 0)
        {
            return -1; // transport_error_indicator set — don't trust anything in this packet
        }

        int adaptationFieldControl = (b[o + 3] >> 4) & 0x3;
        if (adaptationFieldControl != 2 && adaptationFieldControl != 3)
        {
            return -1; // no adaptation field
        }

        int adaptationFieldLength = b[o + 4];
        if (adaptationFieldLength < 7)
        {
            return -1; // a PCR needs the flags byte + 6 PCR bytes
        }

        int flags = b[o + 5];
        if ((flags & 0x10) == 0)
        {
            return -1; // PCR flag not set
        }

        return ((long)b[o + 6] << 25)
            | ((long)b[o + 7] << 17)
            | ((long)b[o + 8] << 9)
            | ((long)b[o + 9] << 1)
            | ((long)(b[o + 10] >> 7) & 0x1);
    }

    /// <summary>
    /// Runs when the upstream copy finishes. Reconnects (after a short backoff) for live/catch-up
    /// streams that were not deliberately closed, otherwise lets the stream end.
    /// </summary>
    /// <param name="task">The completed copy task.</param>
    private void OnUpstreamEnded(Task task)
    {
        _logger.LogInformation("Restream upstream for channel {ChannelId} finished with state {Status}", MediaSource.Id, task.Status);
        try
        {
            _inputStream?.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error closing upstream for channel {ChannelId}", MediaSource.Id);
        }

        _inputStream = null;

        // Let the stream end when it was deliberately closed, when it is finite (VOD/series), or when
        // nobody is watching any more — the last guard stops an abandoned stream from looping forever.
        if (_tokenSource.IsCancellationRequested || !MediaSource.IsInfiniteStream || ConsumerCount <= 0)
        {
            return;
        }

        // Barren-reconnect escalation (paced/timeshift only): a connection that cleanly EOF'd having
        // trimmed plenty but delivered essentially nothing re-served only already-delivered content —
        // the minute-truncated restart is stuck at/behind a provider chunk boundary. Each barren round
        // widens the forward skip, so we lose up to a minute of content instead of freezing the
        // playlist forever. Transport failures (fault, empty body) are NOT barren: they retry at the
        // same start and the overlap trim absorbs whatever the next healthy connection re-serves.
        if (_pace)
        {
            bool cleanEof = task.Status == TaskStatus.RanToCompletion;
            bool reservedOldContent = _trimmedBytes >= BarrenThresholdBytes;
            if (cleanEof && reservedOldContent && _deliveredThisConnection < BarrenThresholdBytes)
            {
                _barrenReconnects = Math.Min(_barrenReconnects + 1, MaxBarrenSkipMinutes);
                _logger.LogWarning(
                    "Restream for channel {ChannelId} reconnect was barren ({Bytes} bytes delivered); next start skips +{Minutes} min.",
                    MediaSource.Id,
                    _deliveredThisConnection,
                    _barrenReconnects);
            }
            else if (_deliveredThisConnection >= BarrenThresholdBytes && _barrenReconnects > 0)
            {
                // The escalated start finally delivered: the content timeline now runs the skipped
                // span ahead of the wall-aligned start, permanently. Fold the skip into the baseline
                // so later reconnects don't re-request — and have to re-trim — the skipped minutes
                // on every reconnect for the rest of the session.
                TimeSpan folded = _skipBaseline + TimeSpan.FromMinutes(_barrenReconnects);
                _skipBaseline = folded < MaxSkipBaseline ? folded : MaxSkipBaseline;
                _barrenReconnects = 0;
                _logger.LogInformation(
                    "Restream for channel {ChannelId} folded barren skip into baseline; reconnect starts now offset by {Baseline}.",
                    MediaSource.Id,
                    _skipBaseline);
            }
        }

        _ = ReconnectAsync();
    }

    /// <summary>
    /// Reconnects the upstream after a short delay, retrying until the consumer disconnects. Keeps the
    /// shared buffer alive so the client keeps reading across the gap instead of hitting an EOF.
    /// </summary>
    private async Task ReconnectAsync()
    {
        try
        {
            // Backoff so an upstream that EOFs instantly can't spin a tight loop.
            await Task.Delay(TimeSpan.FromSeconds(2), _tokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_tokenSource.IsCancellationRequested || ConsumerCount <= 0)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Reconnecting restream upstream for channel {ChannelId}.", MediaSource.Id);

            // Arm the overlap trim for the reconnected input (only once something was delivered;
            // there is nothing to overlap with on a virgin buffer). NOT gated on _pace: plain live
            // reconnects re-serve provider backlog too (12-29 s measured in the field). The
            // per-connection trim state resets in ConnectAndPumpAsync. Logged so a trim that never
            // arms is visible — silent non-engagement was the 0.9.7.0/0.9.7.1 failure mode.
            _trimOverlap = _lastDeliveredClock >= 0;
            if (!_trimOverlap)
            {
                _logger.LogWarning(
                    "Restream for channel {ChannelId} reconnecting WITHOUT overlap trim: no video clock seen yet.",
                    MediaSource.Id);
            }
            else
            {
                _logger.LogInformation(
                    "Restream for channel {ChannelId} arming overlap trim at clock {Clock}.",
                    MediaSource.Id,
                    _lastDeliveredClock);
            }

            await ConnectAndPumpAsync(_tokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Consumer disconnected during reconnect; nothing to do.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restream reconnect failed for channel {ChannelId}; retrying.", MediaSource.Id);
            _ = ReconnectAsync();
        }
    }

    /// <inheritdoc />
    public async Task Close()
    {
        if (_copyTask == null)
        {
            throw new ArgumentNullException("copyTask");
        }

        await _tokenSource.CancelAsync().ConfigureAwait(false);
        await _copyTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Stream GetStream()
    {
        if (_inputStream == null)
        {
            _logger.LogWarning("Restream for channel {ChannelId} was not opened.", MediaSource.Id);
            _ = Open(CancellationToken.None);
        }

        _logger.LogInformation("Opening restream {Count} for channel {ChannelId}.", ConsumerCount, MediaSource.Id);
        return new WrappedBufferReadStream(_buffer);
    }

    /// <summary>
    /// Disposes the fields.
    /// </summary>
    /// <param name="disposing">Whether or not to dispose.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inputStream?.Dispose();
            _buffer.Dispose();
            _tokenSource.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Implement IDisposable.
        // Do not make this method virtual.
        // A derived class should not be able to override this method.
        Dispose(true);
        // This object will be cleaned up by the Dispose method.
        // Therefore, you should call GC.SuppressFinalize to
        // take this object off the finalization queue
        // and prevent finalization code for this object
        // from executing a second time.
        GC.SuppressFinalize(this);
    }
}
