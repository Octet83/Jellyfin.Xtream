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
    private readonly Func<string>? _urlProvider;
    private readonly bool _pace;

    private Task? _copyTask;
    private Stream? _inputStream;

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
    /// <c>start</c> to "now", which — the offset being constant — resumes seamlessly. When null the
    /// original <see cref="MediaSourceInfo.Path"/> is reused on every reconnect.
    /// </param>
    /// <param name="pace">
    /// When true, the upstream is throttled to ~1x real time using the MPEG-TS PCR clock. Required for
    /// catch-up/timeshift feeds, which the provider delivers as a fast download rather than a paced live
    /// stream — without pacing the consumer races to the end and the stream stops after a few seconds.
    /// </param>
    public Restream(IServerApplicationHost appHost, IHttpClientFactory httpClientFactory, ILogger logger, MediaSourceInfo mediaSource, Func<string>? urlProvider = null, bool pace = false)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _urlProvider = urlProvider;
        _pace = pace;
        MediaSource = mediaSource;

        _buffer = new WrappedBufferStream(16 * 1024 * 1024); // 16MiB
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

        _logger.LogInformation("Starting restream for channel {ChannelId}.", MediaSource.Id);

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
        string url = _urlProvider?.Invoke() ?? _url;

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
        Task pump = _pace
            ? PaceCopyAsync(input, _buffer, _tokenSource.Token)
            : input.CopyToAsync(_buffer, _tokenSource.Token);
        _copyTask = pump.ContinueWith(
                OnUpstreamEnded,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
    }

    /// <summary>
    /// Copies an MPEG-TS upstream into the buffer paced to ~1x real time using the stream's PCR clock.
    /// Catch-up/timeshift feeds are delivered as a fast download (many times real time); without pacing
    /// the consumer (FFmpeg) races to the end and the "live" stream stops after seconds, and the ring
    /// buffer overruns ("Reader cannot keep up"). Pacing the writer makes the blocking reader follow at 1x.
    /// </summary>
    /// <param name="input">The upstream content stream.</param>
    /// <param name="output">The shared restream buffer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task PaceCopyAsync(Stream input, Stream output, CancellationToken cancellationToken)
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
            int start = 0;

            // Align to the first TS sync byte once (real streams start aligned, so this is usually a no-op).
            if (!aligned)
            {
                int sync = FindSync(buf, available);
                if (sync < 0)
                {
                    leftover = Math.Min(available, TsPacketSize * 2);
                    Array.Copy(buf, available - leftover, buf, 0, leftover);
                    continue;
                }

                start = sync;
                aligned = true;
            }

            int packets = (available - start) / TsPacketSize;
            int end = start + (packets * TsPacketSize);

            // Accumulate clean PCR deltas (no sleeping here; pacing is applied per chunk below).
            for (int i = start; i < end; i += TsPacketSize)
            {
                long pcr = TryReadPcr(buf, i);
                if (pcr < 0)
                {
                    continue;
                }

                if (lastPcr < 0)
                {
                    lastPcr = pcr;
                    continue;
                }

                long delta = pcr - lastPcr;
                lastPcr = pcr;
                if (delta > 0 && delta <= MaxPcrDelta)
                {
                    mediaElapsedSeconds += delta / 90000.0;
                }
            }

            await output.WriteAsync(buf.AsMemory(start, end - start), cancellationToken).ConfigureAwait(false);
            bytesDelivered += end - start;

            leftover = available - end;
            if (leftover > 0)
            {
                Array.Copy(buf, end, buf, 0, leftover);
            }

            // Pace: sleep if we are ahead by the media clock (PCR, exact 1x) OR by the byte ceiling
            // (anti-race fallback). Whichever says we are further ahead wins.
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

        int adaptationFieldControl = (b[o + 3] >> 4) & 0x3;
        if (adaptationFieldControl != 2 && adaptationFieldControl != 3)
        {
            return -1; // no adaptation field
        }

        int adaptationFieldLength = b[o + 4];
        if (adaptationFieldLength == 0)
        {
            return -1;
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
