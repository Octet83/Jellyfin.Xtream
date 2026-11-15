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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A service for dealing with stream information.
/// </summary>
/// <param name="xtreamClient">Instance of the <see cref="IXtreamClient"/> interface.</param>
public partial class StreamService(IXtreamClient xtreamClient)
{
    /// <summary>
    /// The category automatically assigned to every catch-up capable channel when the Caledonian-time mode is enabled.
    /// </summary>
    public const string CatchupCategoryName = "A l'heure calédo";

    /// <summary>
    /// The id prefix for VOD category channel items.
    /// </summary>
    public const int VodCategoryPrefix = 0x5d774c35;

    /// <summary>
    /// The id prefix for stream channel items.
    /// </summary>
    public const int StreamPrefix = 0x5d774c36;

    /// <summary>
    /// The id prefix for series category channel items.
    /// </summary>
    public const int SeriesCategoryPrefix = 0x5d774c37;

    /// <summary>
    /// The id prefix for series category channel items.
    /// </summary>
    public const int SeriesPrefix = 0x5d774c38;

    /// <summary>
    /// The id prefix for season channel items.
    /// </summary>
    public const int SeasonPrefix = 0x5d774c39;

    /// <summary>
    /// The id prefix for season channel items.
    /// </summary>
    public const int EpisodePrefix = 0x5d774c3a;

    /// <summary>
    /// The id prefix for catchup channel items.
    /// </summary>
    public const int CatchupPrefix = 0x5d774c3b;

    /// <summary>
    /// The id prefix for catchup stream items.
    /// </summary>
    public const int CatchupStreamPrefix = 0x5d774c3c;

    /// <summary>
    /// The id prefix for media source items.
    /// </summary>
    public const int MediaSourcePrefix = 0x5d774c3d;

    /// <summary>
    /// The id prefix for Live TV items.
    /// </summary>
    public const int LiveTvPrefix = 0x5d774c3e;

    /// <summary>
    /// The id prefix for TV EPG items.
    /// </summary>
    public const int EpgPrefix = 0x5d774c3f;

    private static readonly Regex _tagRegex = TagRegex();

    // The quality-variant index needs the provider's whole live catalogue (tens of thousands of
    // streams), so it is kept for a while instead of being rebuilt on every channel open.
    private static readonly TimeSpan _variantTtl = TimeSpan.FromMinutes(15);

    private volatile VariantIndex? _variantIndex;

    /// <summary>
    /// Parses tags in the name of a stream entry.
    /// The name commonly contains tags of the forms:
    /// <list>
    /// <item>[TAG]</item>
    /// <item>|TAG|</item>
    /// <item>| TAG | (with spaces, e.g., | NL |)</item>
    /// </list>
    /// Supports Unicode pipe variants (│, ┃, ｜) in addition to ASCII pipe.
    /// These tags are parsed and returned as separate strings.
    /// The returned title is cleaned from tags and trimmed.
    /// </summary>
    /// <param name="name">The name which should be parsed.</param>
    /// <returns>A <see cref="ParsedName"/> struct containing the cleaned title and parsed tags.</returns>
    public static ParsedName ParseName(string name)
    {
        List<string> tags = [];
        string title = _tagRegex.Replace(
            name,
            (match) =>
            {
                for (int i = 1; i < match.Groups.Count; ++i)
                {
                    Group g = match.Groups[i];
                    if (g.Success)
                    {
                        tags.Add(g.Value);
                    }
                }

                return string.Empty;
            });

        // Tag prefixes separated by the a character in the unicode Block Elements range
        int stripLength = 0;
        for (int i = 0; i < title.Length; i++)
        {
            char c = title[i];
            if (c >= '\u2580' && c <= '\u259F')
            {
                tags.Add(title[stripLength..i].Trim());
                stripLength = i + 1;
            }
        }

        return new ParsedName
        {
            Title = title[stripLength..].Trim(),
            Tags = [.. tags],
        };
    }

    private bool IsConfigured(SerializableDictionary<int, HashSet<int>> config, int category, int id)
    {
        return config.TryGetValue(category, out var values) && (values.Count == 0 || values.Contains(id));
    }

    /// <summary>
    /// Gets an async iterator for the configured channels.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>IAsyncEnumerable{StreamInfo}.</returns>
    public async Task<IEnumerable<StreamInfo>> GetLiveStreams(CancellationToken cancellationToken)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;

        IEnumerable<StreamInfo> streams = await xtreamClient.GetLiveStreamsAsync(Plugin.Instance.Creds, cancellationToken).ConfigureAwait(false);
        return streams.Where((StreamInfo channel) => channel.CategoryId.HasValue && IsConfigured(config.LiveTv, channel.CategoryId.Value, channel.StreamId));
    }

    /// <summary>
    /// Gets an async iterator for the configured channels after applying the configured overrides.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>IAsyncEnumerable{StreamInfo}.</returns>
    public async Task<IEnumerable<StreamInfo>> GetLiveStreamsWithOverrides(CancellationToken cancellationToken)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        IEnumerable<StreamInfo> streams = await GetLiveStreams(cancellationToken).ConfigureAwait(false);
        return streams.Select((StreamInfo stream) =>
        {
            if (config.LiveTvOverrides.TryGetValue(stream.StreamId, out ChannelOverrides? overrides))
            {
                stream.Num = overrides.Number ?? stream.Num;
                stream.Name = overrides.Name ?? stream.Name;
                stream.StreamIcon = overrides.LogoUrl ?? stream.StreamIcon;
            }

            return stream;
        });
    }

    /// <summary>
    /// Drops the cached quality-variant index, so the next lookup fetches the provider catalogue again.
    /// Called when the configuration (selection, credentials, Caledonian mode) changes.
    /// </summary>
    public void InvalidateLiveVariants() => _variantIndex = null;

    /// <summary>
    /// Gets the ways a Live TV channel can be played: one source per quality the provider offers the
    /// channel in ("TF1 HD", "TF1 FHD", "TF1 4K" are separate provider streams), and for catch-up capable
    /// streams in Caledonian mode both the time-shifted source and the real live one.
    /// The first source is the default: the channel's own stream, time-shifted when it can be.
    /// </summary>
    /// <param name="channelStreamId">The Xtream stream id of the channel.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The playable sources, default first, then by decreasing quality.</returns>
    public async Task<IReadOnlyList<LiveSource>> GetLiveSources(int channelStreamId, CancellationToken cancellationToken)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        VariantIndex index = await GetVariantIndex(cancellationToken).ConfigureAwait(false);
        if (!index.ByChannel.TryGetValue(channelStreamId, out IReadOnlyList<Variant>? variants))
        {
            variants = [new Variant(channelStreamId, 0, QualityName.StandardLabel, 0, false, string.Empty)];
        }

        bool forcedLive = config.LiveTvOverrides.TryGetValue(channelStreamId, out ChannelOverrides? overrides)
            && (overrides.ForceLive ?? false);
        bool shiftAllowed = config.LiveAtCaledonianTime && !forcedLive;

        List<LiveSource> sources = [];
        foreach (Variant variant in variants)
        {
            if (shiftAllowed && variant.HasCatchup)
            {
                sources.Add(CreateLiveSource(variant, StreamType.CatchupLive, sources.Count == 0));
            }

            sources.Add(CreateLiveSource(variant, StreamType.Live, sources.Count == 0));
        }

        return sources;
    }

    /// <summary>
    /// Gets the key shared by the configured channels that are the same channel in different
    /// qualities ("TF1 HD" and "TF1 4K" both selected). Clients use it to show them as one entry.
    /// </summary>
    /// <param name="channelStreamId">The Xtream stream id of the channel.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The group key; unique to the channel when its name cannot be parsed.</returns>
    public async Task<string> GetLiveGroupKey(int channelStreamId, CancellationToken cancellationToken)
    {
        VariantIndex index = await GetVariantIndex(cancellationToken).ConfigureAwait(false);
        return index.GroupKeys.TryGetValue(channelStreamId, out string? key) ? key : UngroupedKey(channelStreamId);
    }

    /// <summary>
    /// Gets the selected channels that are hidden because they are another quality of a channel
    /// already shown (<see cref="PluginConfiguration.MergeQualityVariants"/>). Their quality stays
    /// playable as a media source of the channel that represents the group.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The Xtream stream ids to leave out of the Live TV channel list.</returns>
    public async Task<IReadOnlySet<int>> GetMergedAwayLiveStreams(CancellationToken cancellationToken)
    {
        if (!Plugin.Instance.Configuration.MergeQualityVariants)
        {
            return new HashSet<int>();
        }

        VariantIndex index = await GetVariantIndex(cancellationToken).ConfigureAwait(false);
        return index.MergedAway;
    }

    /// <summary>
    /// Orders two qualities of the same channel to pick the one shown in the channel list. The plain
    /// HD stream wins (it is the one providers keep a catch-up archive for, so the one the Caledonian
    /// time-shift needs), then FHD, then the rest by decreasing quality. The catch-up flag itself is
    /// deliberately not used: it flaps on the provider side and the pick must not change between
    /// refreshes (the Jellyfin channel item, its favourites and its EPG hang off it).
    /// </summary>
    private static int CompareRepresentative(Variant a, Variant b, PluginConfiguration config)
    {
        static int Tier(Variant v) => v.Label switch
        {
            "HD" => 0,
            "FHD" => 1,
            _ => v.Label.Contains(' ', StringComparison.Ordinal) ? 3 : 2,
        };

        int result = Tier(a).CompareTo(Tier(b));
        if (result == 0)
        {
            result = b.Rank.CompareTo(a.Rank);
        }

        if (result == 0)
        {
            // A channel the admin customised (number, logo, forced live…) keeps its settings.
            result = config.LiveTvOverrides.ContainsKey(b.StreamId).CompareTo(config.LiveTvOverrides.ContainsKey(a.StreamId));
        }

        if (result == 0)
        {
            result = a.Num.CompareTo(b.Num);
        }

        return result == 0 ? a.StreamId.CompareTo(b.StreamId) : result;
    }

    private static string UngroupedKey(int streamId) => "#" + streamId.ToString(CultureInfo.InvariantCulture);

    private static LiveSource CreateLiveSource(Variant variant, StreamType type, bool isDefault) => new()
    {
        SourceId = ToGuid(MediaSourcePrefix, (int)type, variant.StreamId, 0).ToString(),
        StreamId = variant.StreamId,
        Label = variant.Label,
        Rank = variant.Rank,
        TimeShifted = type == StreamType.CatchupLive,
        IsDefault = isDefault,
        Name = variant.Name,
    };

    /// <summary>
    /// Builds (or returns the cached) index of the quality variants of every configured channel.
    /// Variants are looked up in the provider's whole live catalogue, not only the configured selection:
    /// selecting "TF1 HD" is enough to be offered "TF1 FHD" and "TF1 4K".
    /// </summary>
    private async Task<VariantIndex> GetVariantIndex(CancellationToken cancellationToken)
    {
        VariantIndex? index = _variantIndex;
        if (index is not null && index.Expiry > DateTime.UtcNow)
        {
            return index;
        }

        PluginConfiguration config = Plugin.Instance.Configuration;
        List<StreamInfo> all = await xtreamClient.GetLiveStreamsAsync(Plugin.Instance.Creds, cancellationToken).ConfigureAwait(false);

        Dictionary<string, List<Variant>> byKey = new(StringComparer.Ordinal);
        foreach (StreamInfo stream in all)
        {
            if (!QualityName.TryParse(stream.Name, out QualityName quality))
            {
                continue;
            }

            if (!byKey.TryGetValue(quality.Key, out List<Variant>? group))
            {
                group = [];
                byKey[quality.Key] = group;
            }

            group.Add(new Variant(stream.StreamId, stream.Num, quality.Label, quality.Rank, stream.TvArchive, stream.Name));
        }

        Dictionary<int, IReadOnlyList<Variant>> byChannel = [];
        Dictionary<int, string> groupKeys = [];
        Dictionary<string, Variant> representatives = new(StringComparer.Ordinal);
        foreach (StreamInfo channel in all)
        {
            if (!channel.CategoryId.HasValue || !IsConfigured(config.LiveTv, channel.CategoryId.Value, channel.StreamId))
            {
                continue;
            }

            bool parsed = QualityName.TryParse(channel.Name, out QualityName quality);
            Variant self = new(
                channel.StreamId,
                channel.Num,
                parsed ? quality.Label : QualityName.StandardLabel,
                parsed ? quality.Rank : 0,
                channel.TvArchive,
                channel.Name);

            List<Variant> list = [self];
            if (parsed && byKey.TryGetValue(quality.Key, out List<Variant>? candidates))
            {
                // One stream per quality label: providers often list the same quality several times
                // (regional copies, backups). Prefer the catch-up capable one, then the provider order.
                list.AddRange(candidates
                    .Where(v => v.StreamId != self.StreamId && !string.Equals(v.Label, self.Label, StringComparison.Ordinal))
                    .GroupBy(v => v.Label, StringComparer.Ordinal)
                    .Select(g => g.OrderByDescending(v => v.HasCatchup).ThenBy(v => v.Num).First())
                    .OrderByDescending(v => v.Rank)
                    .ThenBy(v => v.Label, StringComparer.Ordinal));
            }

            byChannel[channel.StreamId] = list;
            string groupKey = parsed ? quality.Key : UngroupedKey(channel.StreamId);
            groupKeys[channel.StreamId] = groupKey;
            if (!representatives.TryGetValue(groupKey, out Variant? current)
                || CompareRepresentative(self, current, config) < 0)
            {
                representatives[groupKey] = self;
            }
        }

        // The selected channels merged into another one: every channel of a group but its representative.
        HashSet<int> mergedAway = [.. groupKeys.Where(kv => representatives[kv.Value].StreamId != kv.Key).Select(kv => kv.Key)];

        index = new VariantIndex(DateTime.UtcNow + _variantTtl, byChannel, groupKeys, mergedAway);
        _variantIndex = index;
        return index;
    }

    /// <summary>
    /// Removes a single Live TV stream from the configured selection (<see cref="PluginConfiguration.LiveTv"/>).
    /// When the channel's category is fully selected (empty set), the full set is materialized first so that only
    /// the requested channel is removed.
    /// </summary>
    /// <param name="streamId">The Xtream stream id to remove from the selection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the channel was found and removed, false otherwise.</returns>
    public async Task<bool> RemoveLiveStreamFromSelection(int streamId, CancellationToken cancellationToken)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        IEnumerable<StreamInfo> streams = await xtreamClient.GetLiveStreamsAsync(Plugin.Instance.Creds, cancellationToken).ConfigureAwait(false);
        StreamInfo? target = streams.FirstOrDefault((StreamInfo s) => s.StreamId == streamId);
        if (target?.CategoryId is not int categoryId || !config.LiveTv.TryGetValue(categoryId, out HashSet<int>? selected))
        {
            return false;
        }

        if (selected.Count == 0)
        {
            // An empty set means "all channels in this category are selected".
            // Materialize the full set so that only the requested channel is removed.
            HashSet<int> all = [.. streams.Where((StreamInfo s) => s.CategoryId == categoryId).Select((StreamInfo s) => s.StreamId)];
            all.Remove(streamId);
            config.LiveTv[categoryId] = all;
        }
        else
        {
            if (!selected.Remove(streamId))
            {
                return false;
            }
        }

        // Drop the channel overrides as the channel is no longer part of the selection.
        config.LiveTvOverrides.Remove(streamId);
        return true;
    }

    /// <summary>
    /// Gets the timezone used to present Catch-up days and program times,
    /// as configured by <see cref="PluginConfiguration.CatchupTimeZoneId"/>.
    /// Falls back to a fixed UTC+11 (New Caledonia) zone when the id is unknown on the host.
    /// </summary>
    /// <returns>The catch-up <see cref="TimeZoneInfo"/>.</returns>
    public static TimeZoneInfo GetCatchupTimeZone()
    {
        string id = Plugin.Instance.Configuration.CatchupTimeZoneId;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = "Pacific/Noumea";
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // New Caledonia is a fixed UTC+11 offset without DST.
            return TimeZoneInfo.CreateCustomTimeZone("Pacific/Noumea", TimeSpan.FromHours(11), "New Caledonia", "New Caledonia");
        }
    }

    /// <summary>
    /// Gets an channel item info for the category.
    /// </summary>
    /// <param name="prefix">The channel category prefix.</param>
    /// <param name="category">The Xtream category.</param>
    /// <returns>A channel item representing the category.</returns>
    public static ChannelItemInfo CreateChannelItemInfo(int prefix, Category category)
    {
        ParsedName parsedName = ParseName(category.CategoryName);
        return new ChannelItemInfo()
        {
            Id = ToGuid(prefix, category.CategoryId, 0, 0).ToString(),
            Name = category.CategoryName,
            Tags = new List<string>(parsedName.Tags),
            Type = ChannelItemType.Folder,
        };
    }

    /// <summary>
    /// Gets an iterator for the configured VOD categories.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>IAsyncEnumerable{StreamInfo}.</returns>
    public async Task<IEnumerable<Category>> GetVodCategories(CancellationToken cancellationToken)
    {
        List<Category> categories = await xtreamClient.GetVodCategoryAsync(Plugin.Instance.Creds, cancellationToken).ConfigureAwait(false);
        return categories.Where((Category category) => Plugin.Instance.Configuration.Vod.ContainsKey(category.CategoryId));
    }

    /// <summary>
    /// Gets an iterator for the configured VOD streams.
    /// </summary>
    /// <param name="categoryId">The Xtream id of the category.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>IAsyncEnumerable{StreamInfo}.</returns>
    public async Task<IEnumerable<StreamInfo>> GetVodStreams(int categoryId, CancellationToken cancellationToken)
    {
        if (!Plugin.Instance.Configuration.Vod.ContainsKey(categoryId))
        {
            return new List<StreamInfo>();
        }

        List<StreamInfo> streams = await xtreamClient.GetVodStreamsByCategoryAsync(Plugin.Instance.Creds, categoryId, cancellationToken).ConfigureAwait(false);
        return streams.Where((StreamInfo stream) => IsConfigured(Plugin.Instance.Configuration.Vod, categoryId, stream.StreamId));
    }

    /// <summary>
    /// Gets an iterator for the configured Series categories.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>IAsyncEnumerable{StreamInfo}.</returns>
    public async Task<IEnumerable<Category>> GetSeriesCategories(CancellationToken cancellationToken)
    {
        List<Category> categories = await xtreamClient.GetSeriesCategoryAsync(Plugin.Instance.Creds, cancellationToken).ConfigureAwait(false);
        return categories.Where((Category category) => Plugin.Instance.Configuration.Series.ContainsKey(category.CategoryId));
    }

    /// <summary>
    /// Gets an iterator for the configured Series.
    /// </summary>
    /// <param name="categoryId">The Xtream id of the category.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>IAsyncEnumerable{StreamInfo}.</returns>
    public async Task<IEnumerable<Series>> GetSeries(int categoryId, CancellationToken cancellationToken)
    {
        if (!Plugin.Instance.Configuration.Series.ContainsKey(categoryId))
        {
            return new List<Series>();
        }

        List<Series> series = await xtreamClient.GetSeriesByCategoryAsync(Plugin.Instance.Creds, categoryId, cancellationToken).ConfigureAwait(false);
        return series.Where((Series series) => IsConfigured(Plugin.Instance.Configuration.Series, series.CategoryId, series.SeriesId));
    }

    /// <summary>
    /// Gets an iterator for the configured seasons in the Series.
    /// </summary>
    /// <param name="seriesId">The Xtream id of the Series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>IAsyncEnumerable{StreamInfo}.</returns>
    public async Task<IEnumerable<Tuple<SeriesStreamInfo, int>>> GetSeasons(int seriesId, CancellationToken cancellationToken)
    {
        SeriesStreamInfo series = await xtreamClient.GetSeriesStreamsBySeriesAsync(Plugin.Instance.Creds, seriesId, cancellationToken).ConfigureAwait(false);
        int categoryId = series.Info.CategoryId;
        if (!IsConfigured(Plugin.Instance.Configuration.Series, categoryId, seriesId))
        {
            return new List<Tuple<SeriesStreamInfo, int>>();
        }

        return series.Episodes.Keys.Select((int seasonId) => new Tuple<SeriesStreamInfo, int>(series, seasonId));
    }

    /// <summary>
    /// Gets an iterator for the configured seasons in the Series.
    /// </summary>
    /// <param name="seriesId">The Xtream id of the Series.</param>
    /// <param name="seasonId">The Xtream id of the Season.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>IAsyncEnumerable{StreamInfo}.</returns>
    public async Task<IEnumerable<Tuple<SeriesStreamInfo, Season?, Episode>>> GetEpisodes(int seriesId, int seasonId, CancellationToken cancellationToken)
    {
        SeriesStreamInfo series = await xtreamClient.GetSeriesStreamsBySeriesAsync(Plugin.Instance.Creds, seriesId, cancellationToken).ConfigureAwait(false);
        Season? season = series.Seasons.FirstOrDefault(s => s.SeasonId == seasonId);
        return series.Episodes[seasonId].Select((Episode episode) => new Tuple<SeriesStreamInfo, Season?, Episode>(series, season, episode));
    }

    private static void StoreBytes(byte[] dst, int offset, int i)
    {
        byte[] intBytes = BitConverter.GetBytes(i);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(intBytes);
        }

        Buffer.BlockCopy(intBytes, 0, dst, offset, 4);
    }

    /// <summary>
    /// Gets a GUID representing the four 32-bit integers.
    /// </summary>
    /// <param name="i0">Bytes 0-3.</param>
    /// <param name="i1">Bytes 4-7.</param>
    /// <param name="i2">Bytes 8-11.</param>
    /// <param name="i3">Bytes 12-15.</param>
    /// <returns>Guid.</returns>
    public static Guid ToGuid(int i0, int i1, int i2, int i3)
    {
        byte[] guid = new byte[16];
        StoreBytes(guid, 0, i0);
        StoreBytes(guid, 4, i1);
        StoreBytes(guid, 8, i2);
        StoreBytes(guid, 12, i3);
        return new Guid(guid);
    }

    /// <summary>
    /// Gets the four 32-bit integers represented in the GUID.
    /// </summary>
    /// <param name="id">The input GUID.</param>
    /// <param name="i0">Bytes 0-3.</param>
    /// <param name="i1">Bytes 4-7.</param>
    /// <param name="i2">Bytes 8-11.</param>
    /// <param name="i3">Bytes 12-15.</param>
    public static void FromGuid(Guid id, out int i0, out int i1, out int i2, out int i3)
    {
        byte[] tmp = id.ToByteArray();
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(tmp);
            i0 = BitConverter.ToInt32(tmp, 12);
            i1 = BitConverter.ToInt32(tmp, 8);
            i2 = BitConverter.ToInt32(tmp, 4);
            i3 = BitConverter.ToInt32(tmp, 0);
        }
        else
        {
            i0 = BitConverter.ToInt32(tmp, 0);
            i1 = BitConverter.ToInt32(tmp, 4);
            i2 = BitConverter.ToInt32(tmp, 8);
            i3 = BitConverter.ToInt32(tmp, 12);
        }
    }

    /// <summary>
    /// Gets the media source information for the given Xtream stream.
    /// </summary>
    /// <param name="type">The stream media type.</param>
    /// <param name="id">The unique identifier of the stream.</param>
    /// <param name="extension">The container extension of the stream.</param>
    /// <param name="restream">Boolean indicating whether or not restreaming is used.</param>
    /// <param name="start">The datetime representing the start time of catcup TV.</param>
    /// <param name="durationMinutes">The duration in minutes of the catcup TV stream.</param>
    /// <param name="videoInfo">The Xtream video info if known.</param>
    /// <param name="audioInfo">The Xtream audio info if known.</param>
    /// <returns>The media source info as <see cref="MediaSourceInfo"/> class.</returns>
    public MediaSourceInfo GetMediaSourceInfo(
        StreamType type,
        int id,
        string? extension = null,
        bool restream = false,
        DateTime? start = null,
        int durationMinutes = 0,
        VideoInfo? videoInfo = null,
        AudioInfo? audioInfo = null)
    {
        string prefix = string.Empty;
        switch (type)
        {
            case StreamType.Series:
                prefix = "/series";
                break;
            case StreamType.Vod:
                prefix = "/movie";
                break;
        }

        PluginConfiguration config = Plugin.Instance.Configuration;
        string uri = $"{config.BaseUrl}{prefix}/{config.Username}/{config.Password}/{id}";
        if (!string.IsNullOrEmpty(extension))
        {
            uri += $".{extension}";
        }

        if (type == StreamType.CatchUp || type == StreamType.CatchupLive)
        {
            string? startString = start?.ToString("yyyy'-'MM'-'dd':'HH'-'mm", CultureInfo.InvariantCulture);
            uri = $"{config.BaseUrl}/streaming/timeshift.php?username={config.Username}&password={config.Password}&stream={id}&start={startString}&duration={durationMinutes}";
        }

        bool isLive = type == StreamType.Live || type == StreamType.CatchupLive;
        return new MediaSourceInfo()
        {
            Container = extension,
            EncoderProtocol = MediaProtocol.Http,
            Id = ToGuid(MediaSourcePrefix, (int)type, id, 0).ToString(),
            IsInfiniteStream = isLive,
            IsRemote = true,
            MediaStreams =
            [
                new()
                {
                    AspectRatio = videoInfo?.AspectRatio,
                    BitDepth = videoInfo?.BitsPerRawSample,
                    Codec = videoInfo?.CodecName,
                    ColorPrimaries = videoInfo?.ColorPrimaries,
                    ColorRange = videoInfo?.ColorRange,
                    ColorSpace = videoInfo?.ColorSpace,
                    ColorTransfer = videoInfo?.ColorTransfer,
                    Height = videoInfo?.Height,
                    Index = videoInfo?.Index ?? -1,
                    IsAVC = videoInfo?.IsAVC,
                    IsInterlaced = true,
                    Level = videoInfo?.Level,
                    PixelFormat = videoInfo?.PixelFormat,
                    Profile = videoInfo?.Profile,
                    Type = MediaStreamType.Video,
                    Width = videoInfo?.Width,
                },
                new()
                {
                    BitRate = audioInfo?.Bitrate,
                    ChannelLayout = audioInfo?.ChannelLayout,
                    Channels = audioInfo?.Channels,
                    Codec = audioInfo?.CodecName,
                    Index = audioInfo?.Index ?? -1,
                    Profile = audioInfo?.Profile,
                    SampleRate = audioInfo?.SampleRate,
                    Type = MediaStreamType.Audio,
                }
            ],
            Name = "default",
            Path = uri,
            Protocol = MediaProtocol.Http,
            RequiresClosing = restream,
            RequiresOpening = restream,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsProbing = true,
        };
    }

    // Matches tags in brackets [TAG] or pipe-delimited |TAG| (with optional spaces and Unicode pipe variants)
    // Pipe variants: | (U+007C), │ (U+2502), ┃ (U+2503), ｜ (U+FF5C)
    [GeneratedRegex(@"\[([^\]]+)\]|[|│┃｜]\s*([^|│┃｜]+?)\s*[|│┃｜]")]
    private static partial Regex TagRegex();

    private sealed record Variant(int StreamId, int Num, string Label, int Rank, bool HasCatchup, string Name);

    private sealed record VariantIndex(DateTime Expiry, Dictionary<int, IReadOnlyList<Variant>> ByChannel, Dictionary<int, string> GroupKeys, HashSet<int> MergedAway);
}
