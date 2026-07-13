using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using ASFUnpaidStuff.ASFExtensions.Games;
using Maxisoft.Utils.Collections.Dictionaries;

namespace Maxisoft.ASF.Reddit;

internal static class OldRedditHtmlParser {
	private const string CommentStartMarker = "<div class=\" thing id-t1_";

	internal static IReadOnlyCollection<RedditGameEntry> ParseGamesFromHtml(string html, int maxEntries = RedditHelper.MaxGameEntry) {
		if (string.IsNullOrWhiteSpace(html)) {
			return [];
		}

		Maxisoft.Utils.Collections.Dictionaries.OrderedDictionary<RedditGameEntry, EmptyStruct> games = new(new GameEntryIdentifierEqualityComparer());
		MatchCollection matches = RedditHelperRegexes.Command().Matches(html);

		foreach (Match match in matches) {
			if (!match.Success) {
				continue;
			}

			string commentHtml = ExtractCommentHtml(html, match.Index, match.Length);

			if (string.IsNullOrWhiteSpace(commentHtml)) {
				continue;
			}

			long date = ExtractDate(commentHtml);

			if (date <= 0) {
				continue;
			}

			string decodedComment = WebUtility.HtmlDecode(commentHtml).Replace('\u00a0', ' ');
			ERedditGameEntryKind kind = ERedditGameEntryKind.None;

			if (RedditHelperRegexes.IsPermanentlyFree().IsMatch(decodedComment) || RedditHelperRegexes.IsFreeToPlay().IsMatch(decodedComment)) {
				kind |= ERedditGameEntryKind.FreeToPlay;
			}

			if (RedditHelperRegexes.IsDlc().IsMatch(decodedComment)) {
				kind = ERedditGameEntryKind.Dlc;
			}

			foreach (Group matchGroup in match.Groups) {
				if (!matchGroup.Name.StartsWith("appid", StringComparison.InvariantCulture)) {
					continue;
				}

				foreach (Capture capture in matchGroup.Captures) {
					if (!GameIdentifier.TryParse(capture.Value, out _)) {
						continue;
					}

					try {
						games.Add(new RedditGameEntry(capture.Value, kind, date), default(EmptyStruct));
					}
					catch (ArgumentException) { }

					if (games.Count >= maxEntries) {
						return (IReadOnlyCollection<RedditGameEntry>) games.Keys;
					}
				}
			}
		}

		return (IReadOnlyCollection<RedditGameEntry>) games.Keys;
	}

	internal static string? GetNextPageAfter(string html) {
		if (string.IsNullOrWhiteSpace(html)) {
			return null;
		}

		MatchCollection matches = OldRedditHtmlParserRegexes.AfterParameter().Matches(html);

		return matches.Count > 0 ? matches[matches.Count - 1].Groups["after"].Value : null;
	}

	private static string ExtractCommentHtml(string html, int commandIndex, int commandLength) {
		int start = html.LastIndexOf(CommentStartMarker, Math.Min(commandIndex, html.Length - 1), StringComparison.OrdinalIgnoreCase);

		if (start < 0) {
			start = Math.Max(0, commandIndex - 4096);
		}

		int end = html.IndexOf(CommentStartMarker, Math.Min(html.Length, commandIndex + Math.Max(commandLength, 1)), StringComparison.OrdinalIgnoreCase);

		if (end < 0) {
			end = Math.Min(html.Length, commandIndex + 8192);
		}

		if (end <= start) {
			return string.Empty;
		}

		return html[start..end];
	}

	private static long ExtractDate(string commentHtml) {
		Match match = OldRedditHtmlParserRegexes.DateTimeAttribute().Match(commentHtml);

		if (!match.Success) {
			return 0;
		}

		string dateTime = WebUtility.HtmlDecode(match.Groups["datetime"].Value);

		return DateTimeOffset.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset parsed) ? parsed.ToUnixTimeSeconds() : 0;
	}
}
