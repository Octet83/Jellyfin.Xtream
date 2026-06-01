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
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Api.Models;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
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
public class XtreamController(IXtreamClient xtreamClient) : ControllerBase
{
    /// <summary>
    /// The category automatically assigned to every channel that supports catch-up.
    /// </summary>
    public const string CatchupCategoryName = "A l'heure calédo";

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
            ? CatchupCategoryName
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
