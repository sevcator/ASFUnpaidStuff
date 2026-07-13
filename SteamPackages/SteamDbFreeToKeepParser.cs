using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

#nullable enable

namespace Maxisoft.ASF.SteamPackages;

internal static partial class SteamDbFreeToKeepParser {
	internal static IReadOnlyCollection<uint> ParsePackageIds(string? content, int requiredDiscountPercent = 100, int maxPackages = 8192) {
		if (string.IsNullOrWhiteSpace(content) || (requiredDiscountPercent <= 0) || (maxPackages <= 0)) {
			return [];
		}

		HashSet<uint> packageIds = [];
		MatchCollection rowMatches = RowRegex().Matches(content);
		string[] segments = rowMatches.Count > 0
			? rowMatches.Select(static match => match.Value).ToArray()
			: content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		foreach (string rawSegment in segments) {
			if (packageIds.Count >= maxPackages) {
				break;
			}

			string segment = WebUtility.HtmlDecode(rawSegment).Replace('\u00a0', ' ');

			if (!HasRequiredDiscount(segment, requiredDiscountPercent)) {
				continue;
			}

			foreach (Match match in PackageIdRegex().Matches(segment)) {
				if (!uint.TryParse(match.Groups["id"].Value, out uint packageId) || (packageId == 0)) {
					continue;
				}

				packageIds.Add(packageId);

				if (packageIds.Count >= maxPackages) {
					break;
				}
			}
		}

		return packageIds.ToArray();
	}

	private static bool HasRequiredDiscount(string segment, int requiredDiscountPercent) {
		string escapedPercent = Regex.Escape(requiredDiscountPercent.ToString(System.Globalization.CultureInfo.InvariantCulture));

		return Regex.IsMatch(segment, $@"(?<!\d){escapedPercent}\s*%", RegexOptions.CultureInvariant) ||
			Regex.IsMatch(segment, $@"data-(?:sort|value)\s*=\s*[""']{escapedPercent}[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	}

	[GeneratedRegex(@"<tr\b[\s\S]*?</tr>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex RowRegex();

	[GeneratedRegex(@"(?:/sub/|(?<![A-Za-z])s/)(?<id>[1-9][0-9]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex PackageIdRegex();
}
