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
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream;

/// <summary>
/// Class LiveTvService.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="LiveTvService"/> class.
/// </remarks>
/// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
/// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
/// <param name="memoryCache">Instance of the <see cref="IMemoryCache"/> interface.</param>
/// <param name="xtreamClient">Instance of the <see cref="IXtreamClient"/> interface.</param>
/// <param name="recordingService">Instance of the <see cref="RecordingService"/> class.</param>
public class LiveTvService(IServerApplicationHost appHost, IHttpClientFactory httpClientFactory, ILogger<LiveTvService> logger, IMemoryCache memoryCache, IXtreamClient xtreamClient, RecordingService recordingService) : ILiveTvService, ISupportsDirectStreamProvider
{
    /// <inheritdoc />
    public string Name => "Xtream Live";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        var overrides = plugin.Configuration.LiveTvOverrides;
        bool caledonianMode = plugin.Configuration.LiveAtCaledonianTime;
        List<ChannelInfo> items = [];
        foreach (StreamInfo channel in await plugin.StreamService.GetLiveStreamsWithOverrides(cancellationToken).ConfigureAwait(false))
        {
            // In Caledonian-time mode only catch-up capable channels can be time-shifted; hide the others.
            if (caledonianMode && !channel.TvArchive)
            {
                continue;
            }

            ParsedName parsed = StreamService.ParseName(channel.Name);

            // Expose the user defined category (if any) as the primary Jellyfin tag so channels can be grouped/filtered.
            List<string> tags = [.. parsed.Tags];
            if (overrides.TryGetValue(channel.StreamId, out ChannelOverrides? channelOverrides)
                && !string.IsNullOrWhiteSpace(channelOverrides.Category))
            {
                tags.RemoveAll(t => string.Equals(t, channelOverrides.Category, StringComparison.OrdinalIgnoreCase));
                tags.Insert(0, channelOverrides.Category);
            }

            items.Add(new ChannelInfo()
            {
                Id = StreamService.ToGuid(StreamService.LiveTvPrefix, channel.StreamId, 0, 0).ToString(),
                Number = channel.Num.ToString(CultureInfo.InvariantCulture),
                ImageUrl = channel.StreamIcon,
                Name = parsed.Title,
                Tags = [.. tags],
            });
        }

        return items;
    }

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        recordingService.CancelTimer(timerId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
    {
        recordingService.CreateTimer(info);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<TimerInfo>>(recordingService.GetAllTimers());
    }

    /// <inheritdoc />
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<SeriesTimerInfo>>(new List<SeriesTimerInfo>());
    }

    /// <inheritdoc />
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
    {
        recordingService.UpdateTimer(updatedTimer);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        MediaSourceInfo source = await GetChannelStream(channelId, string.Empty, cancellationToken).ConfigureAwait(false);
        return [source];
    }

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task CloseLiveStream(string id, CancellationToken cancellationToken)
    {
        logger.LogInformation("Closing livestream {ChannelId}", id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
    {
        return Task.FromResult(new SeriesTimerInfo
        {
            PostPaddingSeconds = 120,
            PrePaddingSeconds = 120,
            RecordAnyChannel = false,
            RecordAnyTime = true,
            RecordNewOnly = false
        });
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        Guid guid = Guid.Parse(channelId);
        StreamService.FromGuid(guid, out int prefix, out int streamId, out int _, out int _);
        if (prefix != StreamService.LiveTvPrefix)
        {
            throw new ArgumentException("Unsupported channel");
        }

        Plugin plugin = Plugin.Instance;

        // Allow reassigning the EPG source to another stream id when the provider mapping is wrong.
        int epgStreamId = streamId;
        if (plugin.Configuration.LiveTvOverrides.TryGetValue(streamId, out ChannelOverrides? channelOverrides)
            && channelOverrides.EpgStreamId is int overrideId)
        {
            epgStreamId = overrideId;
        }

        string key = $"xtream-epg-{channelId}-{epgStreamId}";
        ICollection<ProgramInfo>? items = null;
        if (memoryCache.TryGetValue(key, out ICollection<ProgramInfo>? o))
        {
            items = o;
        }
        else
        {
            items = new List<ProgramInfo>();
            {
                EpgListings epgs = await xtreamClient.GetEpgInfoAsync(plugin.Creds, epgStreamId, cancellationToken).ConfigureAwait(false);
                foreach (EpgInfo epg in epgs.Listings)
                {
                    items.Add(new()
                    {
                        Id = StreamService.ToGuid(StreamService.EpgPrefix, streamId, epg.Id, 0).ToString(),
                        ChannelId = channelId,
                        StartDate = epg.Start,
                        EndDate = epg.End,
                        Name = epg.Title,
                        Overview = epg.Description,
                    });
                }
            }

            memoryCache.Set(key, items, DateTimeOffset.Now.AddMinutes(10));
        }

        return from epg in items
               where epg.EndDate >= startDateUtc && epg.StartDate < endDateUtc
               select epg;
    }

    /// <inheritdoc />
    public Task ResetTuner(string id, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(string channelId, string streamId, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        Guid guid = Guid.Parse(channelId);
        StreamService.FromGuid(guid, out int prefix, out int channel, out int _, out int _);
        if (prefix != StreamService.LiveTvPrefix)
        {
            throw new ArgumentException("Unsupported channel");
        }

        Plugin plugin = Plugin.Instance;
        MediaSourceInfo mediaSourceInfo;
        if (plugin.Configuration.LiveAtCaledonianTime)
        {
            // Serve the channel time-shifted so that the provider's broadcast aligns with the target (Caledonian) wall-clock.
            TimeZoneInfo providerTz = await GetProviderTimeZoneAsync(cancellationToken).ConfigureAwait(false);
            DateTime providerStart = GetCaledonianAlignedStart(providerTz);

            // Allow the stream to keep playing forward (it stays behind the live edge by the timezone offset).
            const int durationMinutes = 24 * 60;
            mediaSourceInfo = plugin.StreamService.GetMediaSourceInfo(StreamType.CatchupLive, channel, start: providerStart, durationMinutes: durationMinutes, restream: true);
        }
        else
        {
            mediaSourceInfo = plugin.StreamService.GetMediaSourceInfo(StreamType.Live, channel, restream: true);
        }

        ILiveStream? stream = currentLiveStreams.Find(stream => stream.TunerHostId == Restream.TunerHost && stream.MediaSource.Id == mediaSourceInfo.Id);

        if (stream == null)
        {
            stream = new Restream(appHost, httpClientFactory, logger, mediaSourceInfo);
            await stream.Open(cancellationToken).ConfigureAwait(false);
        }

        stream.ConsumerCount++;
        return stream;
    }

    /// <summary>
    /// Computes the provider-local wall-clock start time so that the provider's broadcast aligns with the
    /// current wall-clock time in the catch-up (Caledonian) timezone. The most recent past occurrence is used.
    /// </summary>
    /// <param name="providerTz">The provider's timezone.</param>
    /// <returns>The provider-local start time to feed to the timeshift endpoint.</returns>
    private static DateTime GetCaledonianAlignedStart(TimeZoneInfo providerTz)
    {
        TimeZoneInfo caledoTz = StreamService.GetCatchupTimeZone();
        DateTime utcNow = DateTime.UtcNow;
        DateTime caledoNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, caledoTz);
        DateTime providerNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, providerTz);

        DateTime candidate = providerNow.Date + caledoNow.TimeOfDay;
        if (candidate > providerNow)
        {
            candidate = candidate.AddDays(-1);
        }

        return candidate;
    }

    /// <summary>
    /// Resolves (and caches) the Xtream provider's timezone from its reported server info.
    /// Falls back to UTC when the timezone id is unknown on the host.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The provider's <see cref="TimeZoneInfo"/>.</returns>
    private async Task<TimeZoneInfo> GetProviderTimeZoneAsync(CancellationToken cancellationToken)
    {
        const string key = "xtream-provider-tz";
        if (memoryCache.TryGetValue(key, out TimeZoneInfo? cached) && cached is not null)
        {
            return cached;
        }

        PlayerApi info = await xtreamClient.GetUserAndServerInfoAsync(Plugin.Instance.Creds, cancellationToken).ConfigureAwait(false);
        TimeZoneInfo tz = TimeZoneInfo.Utc;
        string id = info.ServerInfo.Timezone;
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                logger.LogWarning(ex, "Unknown Xtream provider timezone '{TimeZone}'; falling back to UTC.", id);
            }
        }

        memoryCache.Set(key, tz, DateTimeOffset.Now.AddHours(6));
        return tz;
    }
}
