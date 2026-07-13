using System;
using ASFUnpaidStuff.Configurations;
using Maxisoft.ASF.Reddit;

namespace Maxisoft.ASF.FreeGames;

internal static class RedditCandidateAgeFilter {
	internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(548);

	internal static bool IsRecent(RedditGameEntry entry, DateTimeOffset now) => IsRecent(entry, now, maxAgeDays: (int) MaxAge.TotalDays);

	internal static bool IsRecent(RedditGameEntry entry, DateTimeOffset now, ASFUnpaidStuffRedditOptions options) => IsRecent(entry, now, options.MaxAgeDays);

	internal static bool IsRecent(RedditGameEntry entry, DateTimeOffset now, int? maxAgeDays) {
		DateTimeOffset? created = ParseEntryDate(entry.Date);

		if (created is null) {
			return false;
		}

		return maxAgeDays is null || created.Value >= now.ToUniversalTime().Subtract(TimeSpan.FromDays(maxAgeDays.Value));
	}

	internal static DateTimeOffset? ParseEntryDate(long rawDate) {
		if (rawDate <= 0) {
			return null;
		}

		try {
			return rawDate > 10_000_000_000
				? DateTimeOffset.FromUnixTimeMilliseconds(rawDate)
				: DateTimeOffset.FromUnixTimeSeconds(rawDate);
		}
		catch (ArgumentOutOfRangeException) {
			return null;
		}
	}
}
