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

namespace Jellyfin.Xtream.Api.Models;

/// <summary>
/// A single EPG (TV guide) entry used to preview/verify a channel's program.
/// </summary>
public class EpgEntryResponse
{
    /// <summary>
    /// Gets or sets the program title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the program description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC start time of the program.
    /// </summary>
    public DateTime Start { get; set; }

    /// <summary>
    /// Gets or sets the UTC end time of the program.
    /// </summary>
    public DateTime End { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the program is currently playing.
    /// </summary>
    public bool NowPlaying { get; set; }
}
