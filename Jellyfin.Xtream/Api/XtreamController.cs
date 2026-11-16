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
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Xtream.Api.Models;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// The Octet Xtream configuration API.
/// </summary>
[ApiController]
[Route("[controller]")]
[Produces(MediaTypeNames.Application.Json)]
public class XtreamController(IXtreamClient xtreamClient, ILibraryManager libraryManager) : ControllerBase
{
    private static CategoryResponse CreateCategoryResponse(Category category) =>
        new()
        {
            Id = category.CategoryId,
            Name = category.CategoryName,
        };

    private static ItemResponse CreateItemResponse(StreamInfo stream) =>
        new()
        {
            Id = stream.StreamId,
            Name = stream.Name,
            HasCatchup = stream.TvArchive,
            CatchupDuration = stream.TvArchiveDuration,
        };

    private static ItemResponse CreateItemResponse(Series series) =>
        new()
        {
            Id = series.SeriesId,
            Name = series.Name,
            HasCatchup = false,
            CatchupDuration = 0,
        };

    private static ChannelResponse CreateChannelResponse(
        StreamInfo stream,
        Dictionary<int, string> categoryNames,
        SerializableDictionary<int, ChannelOverrides> overrides)
    {
        int categoryId = stream.CategoryId ?? 0;
        categoryNames.TryGetValue(categoryId, out string? categoryName);

        ParsedName parsed = StreamService.ParseName(stream.Name);

        // Channels that support catch-up are all grouped under a single dedicated category.
        string suggested = stream.TvArchive
            ? StreamService.CatchupCategoryName
            : parsed.Tags.FirstOrDefault()
                ?? (string.IsNullOrEmpty(categoryName) ? string.Empty : categoryName);

        overrides.TryGetValue(stream.StreamId, out ChannelOverrides? channelOverrides);

        return new ChannelResponse
        {
            Id = stream.StreamId,
            LogoUrl = stream.StreamIcon,
            Name = stream.Name,
            Number = stream.Num,
            XtreamCategoryId = categoryId,
            XtreamCategoryName = categoryName ?? string.Empty,
            SuggestedCategory = suggested,
            Category = channelOverrides?.Category,
            EpgChannelId = stream.EpgChannelId,
            EpgStreamId = channelOverrides?.EpgStreamId,
            HasCatchup = stream.TvArchive,
            CatchupDuration = stream.TvArchiveDuration,
        };
    }

    /// <summary>
    /// Test the configured provider.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the categories.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("TestProvider")]
    public async Task<ActionResult<ProviderTestResponse>> TestProvider(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        PlayerApi info = await xtreamClient.GetUserAndServerInfoAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
        return Ok(new ProviderTestResponse()
        {
            ActiveConnections = info.UserInfo.ActiveCons,
            ExpiryDate = info.UserInfo.ExpDate,
            MaxConnections = info.UserInfo.MaxConnections,
            ServerTime = info.ServerInfo.TimeNow,
            ServerTimezone = info.ServerInfo.Timezone,
            Status = info.UserInfo.Status,
            SupportsMpegTs = info.UserInfo.AllowedOutputFormats.Contains("ts"),
        });
    }

    /// <summary>
    /// Get all Live TV categories.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the categories.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("LiveCategories")]
    public async Task<ActionResult<IEnumerable<CategoryResponse>>> GetLiveCategories(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<Category> categories = await xtreamClient.GetLiveCategoryAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
        return Ok(categories.Select(CreateCategoryResponse));
    }

    /// <summary>
    /// Get all Live TV streams for the given category.
    /// </summary>
    /// <param name="categoryId">The category for which to fetch the streams.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the streams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("LiveCategories/{categoryId}")]
    public async Task<ActionResult<IEnumerable<StreamInfo>>> GetLiveStreams(int categoryId, CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<StreamInfo> streams = await xtreamClient.GetLiveStreamsByCategoryAsync(
          plugin.Creds,
          categoryId,
          cancellationToken).ConfigureAwait(false);
        return Ok(streams.Select(CreateItemResponse));
    }

    /// <summary>
    /// Get all VOD categories.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the categories.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("VodCategories")]
    public async Task<ActionResult<IEnumerable<CategoryResponse>>> GetVodCategories(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<Category> categories = await xtreamClient.GetVodCategoryAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
        return Ok(categories.Select(CreateCategoryResponse));
    }

    /// <summary>
    /// Get all VOD streams for the given category.
    /// </summary>
    /// <param name="categoryId">The category for which to fetch the streams.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the streams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("VodCategories/{categoryId}")]
    public async Task<ActionResult<IEnumerable<StreamInfo>>> GetVodStreams(int categoryId, CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<StreamInfo> streams = await xtreamClient.GetVodStreamsByCategoryAsync(
          plugin.Creds,
          categoryId,
          cancellationToken).ConfigureAwait(false);
        return Ok(streams.Select(CreateItemResponse));
    }

    /// <summary>
    /// Get all Series categories.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the categories.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("SeriesCategories")]
    public async Task<ActionResult<IEnumerable<CategoryResponse>>> GetSeriesCategories(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<Category> categories = await xtreamClient.GetSeriesCategoryAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
        return Ok(categories.Select(CreateCategoryResponse));
    }

    /// <summary>
    /// Get all Series streams for the given category.
    /// </summary>
    /// <param name="categoryId">The category for which to fetch the streams.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the streams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("SeriesCategories/{categoryId}")]
    public async Task<ActionResult<IEnumerable<StreamInfo>>> GetSeriesStreams(int categoryId, CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<Series> series = await xtreamClient.GetSeriesByCategoryAsync(
          plugin.Creds,
          categoryId,
          cancellationToken).ConfigureAwait(false);
        return Ok(series.Select(CreateItemResponse));
    }

    /// <summary>
    /// Get all configured TV channels, enriched with category and override information.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the channels.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("LiveTv")]
    public async Task<ActionResult<IEnumerable<ChannelResponse>>> GetLiveTvChannels(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<Category> categories = await xtreamClient.GetLiveCategoryAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string> categoryNames = categories
            .GroupBy(c => c.CategoryId)
            .ToDictionary(g => g.Key, g => g.First().CategoryName);

        SerializableDictionary<int, ChannelOverrides> overrides = plugin.Configuration.LiveTvOverrides;
        IEnumerable<StreamInfo> streams = await plugin.StreamService.GetLiveStreams(cancellationToken).ConfigureAwait(false);
        var channels = streams.Select(stream => CreateChannelResponse(stream, categoryNames, overrides)).ToList();
        return Ok(channels);
    }

    /// <summary>
    /// Get the playable sources of every Live TV channel: the qualities the provider offers each
    /// channel in (HD, FHD, 4K, …) and whether each plays time-shifted or as the real live feed.
    /// Open to every signed-in user: clients use it to show quality badges and a quality picker, then
    /// pass the chosen <c>SourceId</c> as <c>MediaSourceId</c> when opening the channel.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing, per channel, its sources (default first).</returns>
    [Authorize]
    [HttpGet("LiveTv/Sources")]
    public async Task<ActionResult<IEnumerable<ChannelSourcesResponse>>> GetLiveTvSources(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        IReadOnlyList<BaseItem> items = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvChannel],
        });

        List<ChannelSourcesResponse> result = [];
        foreach (BaseItem item in items)
        {
            // Only this plugin's channels: their external id is a GUID carrying the Xtream stream id.
            if (!Guid.TryParse(item.ExternalId, out Guid externalId))
            {
                continue;
            }

            StreamService.FromGuid(externalId, out int prefix, out int streamId, out int _, out int _);
            if (prefix != StreamService.LiveTvPrefix)
            {
                continue;
            }

            result.Add(new ChannelSourcesResponse
            {
                ItemId = item.Id.ToString("N", CultureInfo.InvariantCulture),
                StreamId = streamId,
                GroupKey = await plugin.StreamService.GetLiveGroupKey(streamId, cancellationToken).ConfigureAwait(false),
                Sources = await plugin.StreamService.GetLiveSources(streamId, cancellationToken).ConfigureAwait(false),
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// Get the channels of a language for the selection page: one entry per channel whatever its
    /// qualities.
    /// </summary>
    /// <param name="language">The language prefix of the provider categories, e.g. "FR".</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the channels.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("LiveGroups")]
    public async Task<ActionResult<IEnumerable<LiveGroupResponse>>> GetLiveGroups([FromQuery] string? language, CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        string wanted = string.IsNullOrWhiteSpace(language) ? plugin.Configuration.LiveLanguage : language;
        return Ok(await plugin.StreamService.GetLiveGroups(wanted, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Replace the Live TV selection of a language by the given channels. Selecting a channel enables
    /// the stream carrying its catch-up archive when there is one; all its qualities are then offered
    /// as sources. The selection is written to disk but the Live TV channels are NOT refreshed here:
    /// the page saves the plugin configuration right after, and that save is what queues the guide
    /// refresh. Queuing it from both calls made the second one cancel the first, and the refresh
    /// never ran.
    /// </summary>
    /// <param name="language">The language prefix of the provider categories, e.g. "FR".</param>
    /// <param name="keys">The keys of the channels to keep selected.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>The number of channels selected.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("LiveGroups")]
    public async Task<ActionResult<int>> SetLiveGroups([FromQuery] string? language, [FromBody] IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        string wanted = string.IsNullOrWhiteSpace(language) ? plugin.Configuration.LiveLanguage : language;
        int selected = await plugin.StreamService.SetLiveGroupSelection(wanted, keys, cancellationToken).ConfigureAwait(false);
        plugin.Configuration.LiveLanguage = wanted.Trim().ToUpperInvariant();
        plugin.SaveConfiguration();
        plugin.StreamService.InvalidateLiveVariants();
        return Ok(selected);
    }

    /// <summary>
    /// Get a short EPG (TV guide) preview for the given stream, used to verify the program mapping.
    /// Returns the currently playing program followed by the upcoming entries.
    /// </summary>
    /// <param name="streamId">The Xtream stream id whose EPG should be fetched.</param>
    /// <param name="limit">The maximum number of entries to return.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the EPG entries.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Epg/{streamId}")]
    public async Task<ActionResult<IEnumerable<EpgEntryResponse>>> GetEpg(int streamId, [FromQuery] int limit, CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        EpgListings epgs = await xtreamClient.GetEpgInfoAsync(plugin.Creds, streamId, cancellationToken).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        int max = limit > 0 ? limit : 5;

        var entries = epgs.Listings
            .OrderBy(e => e.Start)
            .Where(e => e.End >= now)
            .Take(max)
            .Select(e => new EpgEntryResponse
            {
                Title = e.Title,
                Description = e.Description,
                Start = e.Start,
                End = e.End,
                NowPlaying = e.Start <= now && e.End > now,
            })
            .ToList();
        return Ok(entries);
    }

    /// <summary>
    /// Removes a channel from the Live TV selection (disables it). Persists the configuration and triggers a refresh.
    /// </summary>
    /// <param name="streamId">The Xtream stream id of the channel to disable.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>NoContent on success, NotFound when the channel is not part of the selection.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("LiveTv/{streamId}/Disable")]
    public async Task<ActionResult> DisableChannel(int streamId, CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        bool removed = await plugin.StreamService.RemoveLiveStreamFromSelection(streamId, cancellationToken).ConfigureAwait(false);
        if (!removed)
        {
            return NotFound();
        }

        plugin.UpdateConfiguration(plugin.Configuration);
        return NoContent();
    }
}
