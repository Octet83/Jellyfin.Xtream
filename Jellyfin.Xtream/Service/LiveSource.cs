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

namespace Jellyfin.Xtream.Service;

/// <summary>
/// One way of playing a Live TV channel: a quality variant (a provider stream) in a given mode
/// (time-shifted to the catch-up timezone, or the real live feed).
/// </summary>
public class LiveSource
{
    /// <summary>
    /// Gets the Jellyfin media source id. Clients pass it as <c>MediaSourceId</c> when opening the
    /// channel to play this source.
    /// </summary>
    public string SourceId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the Xtream stream id carrying this quality.
    /// </summary>
    public int StreamId { get; init; }

    /// <summary>
    /// Gets the short quality label, e.g. "HD", "FHD", "4K" or "FHD RAW".
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Gets the quality rank, higher is better (0 unknown, 1 SD, 2 HD, 3 FHD, 4 4K, 5 8K).
    /// </summary>
    public int Rank { get; init; }

    /// <summary>
    /// Gets a value indicating whether this source plays time-shifted to the catch-up timezone
    /// (<c>true</c>) or is the provider's real live feed (<c>false</c>).
    /// </summary>
    public bool TimeShifted { get; init; }

    /// <summary>
    /// Gets a value indicating whether this is the source played when the client names none.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// Gets the raw provider name of the stream.
    /// </summary>
    public string Name { get; init; } = string.Empty;
}
