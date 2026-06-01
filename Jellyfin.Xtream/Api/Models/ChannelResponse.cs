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

namespace Jellyfin.Xtream.Api.Models;

/// <summary>
/// Override configuration for a Live TV channel.
/// </summary>
public class ChannelResponse
{
    /// <summary>
    /// Gets or sets the Xtream API id of the TV channel.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the TV channel number.
    /// </summary>
    public int Number { get; set; }

    /// <summary>
    /// Gets or sets the TV channel name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the url of the channel logo.
    /// </summary>
    public string LogoUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Xtream category id the channel belongs to.
    /// </summary>
    public int XtreamCategoryId { get; set; }

    /// <summary>
    /// Gets or sets the Xtream category name the channel belongs to.
    /// </summary>
    public string XtreamCategoryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the automatically suggested category for the channel
    /// (derived from name tags, the Xtream category, or catch-up support).
    /// </summary>
    public string SuggestedCategory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user defined category override currently configured for the channel.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Gets or sets the Xtream epg_channel_id (XMLTV identifier) reported by the provider.
    /// </summary>
    public string EpgChannelId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the configured EPG source override (an other Xtream stream id) if any.
    /// </summary>
    public int? EpgStreamId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the channel supports catch-up (archive).
    /// </summary>
    public bool HasCatchup { get; set; }

    /// <summary>
    /// Gets or sets the catch-up archive duration in days.
    /// </summary>
    public int CatchupDuration { get; set; }
}
