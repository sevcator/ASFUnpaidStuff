using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ASFUnpaidStuff.Configurations;
using Maxisoft.ASF.HttpClientSimple;
using Maxisoft.Utils.Collections.Dictionaries;

namespace Maxisoft.ASF.Reddit;

internal static class OldRedditHelper {
	public static async ValueTask<IReadOnlyCollection<RedditGameEntry>> GetGames(SimpleHttpClient httpClient, ASFUnpaidStuffRedditOptions options, uint retry = 5, CancellationToken cancellationToken = default) {
		Maxisoft.Utils.Collections.Dictionaries.OrderedDictionary<RedditGameEntry, EmptyStruct> games = new(new GameEntryIdentifierEqualityComparer());
		HashSet<string> visitedAfterTokens = new(StringComparer.OrdinalIgnoreCase);
		string? after = null;
		int maxPages = Math.Max(1, options.MaxPages);
		int pageLimit = Math.Clamp(options.PageLimit, 1, 100);
		int maxEntries = Math.Max(1, options.MaxEntries);

		for (int page = 0; page < maxPages; page++) {
			cancellationToken.ThrowIfCancellationRequested();

			string html = await GetPage(httpClient, after, pageLimit, retry, cancellationToken).ConfigureAwait(false);
			IReadOnlyCollection<RedditGameEntry> pageGames = OldRedditHtmlParser.ParseGamesFromHtml(html, maxEntries);

			foreach (RedditGameEntry game in pageGames) {
				try {
					games.Add(game, default(EmptyStruct));
				}
				catch (ArgumentException) { }

				if (games.Count >= maxEntries) {
					return (IReadOnlyCollection<RedditGameEntry>) games.Keys;
				}
			}

			after = OldRedditHtmlParser.GetNextPageAfter(html);

			if (string.IsNullOrWhiteSpace(after) || !visitedAfterTokens.Add(after)) {
				break;
			}
		}

		return (IReadOnlyCollection<RedditGameEntry>) games.Keys;
	}

	private static async Task<string> GetPage(SimpleHttpClient httpClient, string? after, int pageLimit, uint retry, CancellationToken cancellationToken) {
		HttpStreamResponse? response = null;

		Dictionary<string, string> headers = new() {
			{ "Pragma", "no-cache" },
			{ "Cache-Control", "no-cache" },
			{ "Accept", "text/html,application/xhtml+xml" },
			{ "Sec-Fetch-Site", "none" },
			{ "Sec-Fetch-Mode", "navigate" },
			{ "Sec-Fetch-Dest", "document" }
		};

		for (int t = 0; t < retry; t++) {
			try {
#pragma warning disable CA2000
				response = await httpClient.GetStreamAsync(GetUrl(after, pageLimit), headers, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2000

				if (!response.Response.IsSuccessStatusCode) {
					throw new RedditServerException($"old reddit http error code is {response.StatusCode}", response.StatusCode);
				}

				return await response.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception e) when (e is IOException or RedditServerException or HttpRequestException) {
				if (t + 1 == retry) {
					throw;
				}

				cancellationToken.ThrowIfCancellationRequested();
			}
			finally {
				if (response is not null) {
					await response.DisposeAsync().ConfigureAwait(false);
				}

				response = null;
			}

			await Task.Delay((2 << (t + 1)) * 100, cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
		}

		cancellationToken.ThrowIfCancellationRequested();

		throw new InvalidOperationException("This should never happen");
	}

	private static Uri GetUrl(string? after, int pageLimit) {
		string query = $"sort=new&limit={pageLimit}";

		if (!string.IsNullOrWhiteSpace(after)) {
			query += "&after=" + Uri.EscapeDataString(after);
		}

		return new Uri($"https://old.reddit.com/user/{RedditHelper.User}/comments/?{query}", UriKind.Absolute);
	}
}
