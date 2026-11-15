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

using System.Collections.Generic;
using Jellyfin.Xtream.Service;

namespace Jellyfin.Xtream.Api.Models;

/// <summary>
/// The playable sources (quality variants) of a Live TV channel.
/// </summary>
public class ChannelSourcesResponse
{
    /// <summary>
    /// Gets or sets the Jellyfin item id of the channel (the id clients get from /LiveTv/Channels).
    /// </summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Xtream stream id of the channel.
    /// </summary>
    public int StreamId { get; set; }

    /// <summary>
    /// Gets or sets the key shared by the channels that are the same channel in different qualities
    /// (e.g. "TF1 HD" and "TF1 4K" both selected). Clients can show them as a single entry.
    /// </summary>
    public string GroupKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the playable sources, the default one first.
    /// </summary>
    public IReadOnlyList<LiveSource> Sources { get; set; } = [];
}
