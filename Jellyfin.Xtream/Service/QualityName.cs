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
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A provider channel name split into the channel it designates and the quality it is offered in.
/// Providers list every quality of a channel as a separate stream ("FR| TF1 HD", "FR| TF1 FHD",
/// "FR| TF1 4K", "FR| TF1 FHD ᴿᴬᵂ"); streams sharing the same <see cref="Key"/> are variants of one channel.
/// </summary>
/// <param name="Key">The grouping key shared by every quality variant of a channel: country prefix plus the name stripped of quality tokens.</param>
/// <param name="Label">The short quality label, e.g. "HD", "FHD", "4K" or "FHD RAW".</param>
/// <param name="Rank">The quality rank, higher is better; 0 when the name carries no quality token.</param>
public readonly partial record struct QualityName(string Key, string Label, int Rank)
{
    /// <summary>
    /// The label used when a name carries no quality token.
    /// </summary>
    public const string StandardLabel = "Standard";

    private static readonly string[] _tierLabels = [StandardLabel, "SD", "HD", "FHD", "4K", "8K"];

    /// <summary>
    /// Parses a provider channel name.
    /// </summary>
    /// <param name="name">The raw provider name.</param>
    /// <param name="result">The parsed name when the method returns <c>true</c>.</param>
    /// <returns><c>false</c> for group separators ("##### FRANCE 4K #####") and names made only of quality tokens.</returns>
    public static bool TryParse(string? name, out QualityName result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(name) || name.Contains('#', StringComparison.Ordinal))
        {
            return false;
        }

        string folded = Fold(name);
        string prefix = string.Empty;
        Match prefixMatch = PrefixRegex().Match(folded);
        if (prefixMatch.Success)
        {
            prefix = prefixMatch.Groups[1].Value;
            folded = folded[prefixMatch.Length..];
        }

        int rank = 0;
        bool raw = false;
        bool hevc = false;
        List<string> rest = [];
        foreach (Match token in TokenRegex().Matches(folded))
        {
            switch (token.Value)
            {
                case "LQ":
                case "SD":
                    rank = Math.Max(rank, 1);
                    break;
                case "HD":
                    rank = Math.Max(rank, 2);
                    break;
                case "FHD":
                case "FULLHD":
                case "1080P":
                    rank = Math.Max(rank, 3);
                    break;
                case "UHD":
                case "4K":
                case "2160P":
                case "3840P":
                    rank = Math.Max(rank, 4);
                    break;
                case "8K":
                    rank = Math.Max(rank, 5);
                    break;
                case "RAW":
                    raw = true;
                    break;
                case "HEVC":
                case "H265":
                    hevc = true;
                    break;
                case "HDR":
                case "50FPS":
                case "60FPS":
                    break;
                default:
                    rest.Add(token.Value);
                    break;
            }
        }

        if (rest.Count == 0)
        {
            return false;
        }

        string label = _tierLabels[rank];
        if (raw)
        {
            label += " RAW";
        }

        if (hevc)
        {
            label += " HEVC";
        }

        // Spaces are dropped from the key: providers spell the same channel "6TER" and "6 TER",
        // "BFMTV" and "BFM TV" depending on the quality category.
        result = new QualityName(prefix + "|" + string.Concat(rest), label, rank);
        return true;
    }

    /// <summary>
    /// Upper-cases the name, strips accents and maps the small-capital / superscript letters providers
    /// decorate names with ("ᴿᴬᵂ", "ᵁᴴᴰ") to plain letters.
    /// </summary>
    private static string Fold(string name)
    {
        string decomposed = name.Normalize(NormalizationForm.FormKD);
        StringBuilder sb = new(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }

        return sb.ToString();
    }

    // Country / group prefix: "FR| TF1 HD", "SP│ BEIN SPORTS 1".
    [GeneratedRegex(@"^\s*([A-Z0-9]{2,4})\s*[|│┃｜]\s*")]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"[A-Z0-9+]+")]
    private static partial Regex TokenRegex();
}
