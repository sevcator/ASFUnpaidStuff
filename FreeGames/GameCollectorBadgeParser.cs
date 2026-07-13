using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Maxisoft.ASF.FreeGames;

#nullable enable

internal static partial class GameCollectorBadgeParser {
	public static ulong? ParseOwnedGamesCount(string? html) {
		if (string.IsNullOrWhiteSpace(html)) {
			return null;
		}

		Match match = OwnedGamesRegex().Match(html);

		if (!match.Success) {
			return null;
		}

		string value = match.Groups["count"].Value.Replace(",", "", StringComparison.Ordinal);

		return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong count) ? count : null;
	}

	[GeneratedRegex(@"(?<count>\d[\d,]*)\s+games?\s+owned", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
	private static partial Regex OwnedGamesRegex();
}
