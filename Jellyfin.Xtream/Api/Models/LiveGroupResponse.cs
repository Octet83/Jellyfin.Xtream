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

namespace Jellyfin.Xtream.Api.Models;

/// <summary>
/// A channel of the provider catalogue for the selection page: every stream that is the same channel
/// in the same language, whatever its quality.
/// </summary>
public class LiveGroupResponse
{
    /// <summary>
    /// Gets or sets the key identifying the channel (language + normalised name).
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel name without prefix nor quality.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider category the channel is filed under, without prefix nor quality.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the qualities the channel is offered in, best first.
    /// </summary>
    public IReadOnlyList<string> Qualities { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether one of the streams has a catch-up archive (so the
    /// channel can be served time-shifted).
    /// </summary>
    public bool HasCatchup { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the channel only exists in looping / on-demand /
    /// pay-per-view categories rather than as a TV channel.
    /// </summary>
    public bool OnDemand { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the channel is part of the Live TV selection.
    /// </summary>
    public bool Selected { get; set; }

    /// <summary>
    /// Gets or sets the number of provider streams grouped under the channel.
    /// </summary>
    public int StreamCount { get; set; }
}
