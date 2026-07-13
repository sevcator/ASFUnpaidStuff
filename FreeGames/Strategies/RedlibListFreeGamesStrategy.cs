using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using Maxisoft.ASF.HttpClientSimple;
using Maxisoft.ASF.Reddit;
using Maxisoft.ASF.Redlib;
using Maxisoft.ASF.Redlib.Html;
using Maxisoft.ASF.Redlib.Instances;

// ReSharper disable once CheckNamespace
namespace Maxisoft.ASF.FreeGames.Strategies;

[SuppressMessage("ReSharper", "RedundantNullableFlowAttribute")]
public sealed class RedlibListFreeGamesStrategy : IListFreeGamesStrategy {
	private readonly SemaphoreSlim DownloadSemaphore = new(4, 4);
	private readonly CachedRedlibInstanceListStorage InstanceListCache = new(Array.Empty<Uri>(), DateTimeOffset.MinValue);

	public void Dispose() => DownloadSemaphore.Dispose();

	public async Task<IReadOnlyCollection<RedditGameEntry>> GetGames([NotNull] ListFreeGamesContext context, CancellationToken cancellationToken) {
		cancellationToken.ThrowIfCancellationRequested();

		CachedRedlibInstanceList instanceList = new(context.Options, InstanceListCache);

		List<Uri> instances = await instanceList.ListInstances(context.HttpClientFactory.CreateForGithub(), cancellationToken).ConfigureAwait(false);
		instances = Shuffle(instances);
		using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(60_000);

		LinkedList<Task<IReadOnlyCollection<RedditGameEntry>>> tasks = [];
		Task<IReadOnlyCollection<RedditGameEntry>>[] allTasks = [];

		try {
			foreach (Uri uri in instances) {
				tasks.AddLast(DownloadUsingInstance(context.HttpClient.Value, uri, context.Options.Reddit, context.Retry, cts.Token));
			}

			allTasks = tasks.ToArray();
			IReadOnlyCollection<RedditGameEntry> result = await MonitorDownloads(tasks, cts.Token).ConfigureAwait(false);

			if (result.Count > 0) {
				return result;
			}
		}
		finally {
			await cts.CancelAsync().ConfigureAwait(false);

			try {
				await Task.WhenAll(allTasks).ConfigureAwait(false);
			}
			catch (Exception) {
				// ignored; observing all download task exceptions prevents UnobservedTaskException noise
			}
		}

		List<Exception> exceptions = new(allTasks.Length);
		exceptions.AddRange(from task in allTasks where task.IsCanceled || task.IsFaulted select IListFreeGamesStrategy.ExceptionFromTask(task));

		switch (exceptions.Count) {
			case 1:
				throw exceptions[0];
			case > 0:
				throw new AggregateException(exceptions);
			default:
				cts.Token.ThrowIfCancellationRequested();

				throw new InvalidOperationException("This should never happen");
		}
	}

	/// <summary>
	///     Gets the Date value from the HTTP response headers.
	/// </summary>
	/// <param name="response">The HTTP response.</param>
	/// <returns>The Date from the HTTP headers, or null if not present.</returns>
	public static DateTimeOffset? GetDateFromHeaders([NotNull] HttpResponseMessage response) => response.Headers.Date;

	private async Task<RedlibPageDownloadResult> DoDownloadUsingInstance(SimpleHttpClient client, Uri uri, CancellationToken cancellationToken) {
		await DownloadSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
		string content;
		DateTimeOffset date = default;

		try {
#pragma warning disable CAC001
#pragma warning disable CA2007
			await using HttpStreamResponse resp = await client.GetStreamAsync(uri, cancellationToken: cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2007
#pragma warning restore CAC001

			if (!resp.HasValidStream) {
				throw new HttpRequestRedlibException("invalid stream for " + uri) {
					Uri = uri,
					StatusCode = resp.StatusCode
				};
			}
			else if (!resp.StatusCode.IsSuccessCode()) {
				throw new HttpRequestRedlibException($"invalid status code {resp.StatusCode} for {uri}") {
					Uri = uri,
					StatusCode = resp.StatusCode
				};
			}
			else {
				content = await resp.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

				date = GetDateFromHeaders(resp.Response) ?? date;
			}
		}
		finally {
			DownloadSemaphore.Release();
		}

		IReadOnlyCollection<RedlibGameEntry> entries = RedlibHtmlParser.ParseGamesFromHtml(content);
		string? after = RedlibHtmlParser.GetNextPageAfter(content);
		DateTimeOffset now = DateTimeOffset.Now;

		if ((date == default(DateTimeOffset)) || ((now - date).Duration() > TimeSpan.FromDays(1))) {
			date = now;
		}

		long dateMillis = date.ToUnixTimeMilliseconds();

		List<RedditGameEntry> redditGameEntries = [];

		// ReSharper disable once LoopCanBeConvertedToQuery
		foreach (RedlibGameEntry entry in entries) {
			redditGameEntries.Add(entry.ToRedditGameEntry(dateMillis));
		}

		return new RedlibPageDownloadResult(redditGameEntries, after);
	}

	private async Task<IReadOnlyCollection<RedditGameEntry>> DownloadUsingInstance(SimpleHttpClient client, Uri uri, ASFUnpaidStuff.Configurations.ASFUnpaidStuffRedditOptions options, uint retry, CancellationToken cancellationToken) {
		Maxisoft.Utils.Collections.Dictionaries.OrderedDictionary<RedditGameEntry, EmptyStruct> games = new(new GameEntryIdentifierEqualityComparer());
		HashSet<string> visitedAfterTokens = new(StringComparer.OrdinalIgnoreCase);
		string? after = null;
		int maxPages = Math.Max(1, options.MaxPages);
		int pageLimit = Math.Clamp(options.PageLimit, 1, 100);
		int maxEntries = Math.Max(1, options.MaxEntries);

		for (int page = 0; page < maxPages; page++) {
			Uri fullUrl = BuildUserCommentsUri(uri, after, pageLimit);
			RedlibPageDownloadResult pageResult = await DownloadPageWithRetry(client, fullUrl, retry, cancellationToken).ConfigureAwait(false);

			foreach (RedditGameEntry game in pageResult.Entries) {
				try {
					games.Add(game, default(EmptyStruct));
				}
				catch (ArgumentException) { }

				if (games.Count >= maxEntries) {
					return (IReadOnlyCollection<RedditGameEntry>) games.Keys;
				}
			}

			after = pageResult.After;

			if (string.IsNullOrWhiteSpace(after) || !visitedAfterTokens.Add(after)) {
				break;
			}
		}

		return (IReadOnlyCollection<RedditGameEntry>) games.Keys;
	}

	private async Task<RedlibPageDownloadResult> DownloadPageWithRetry(SimpleHttpClient client, Uri fullUrl, uint retry, CancellationToken cancellationToken) {
		for (int t = 0; t < retry; t++) {
			try {
				return await DoDownloadUsingInstance(client, fullUrl, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception) {
				if ((t == retry - 1) || cancellationToken.IsCancellationRequested) {
					throw;
				}

				await Task.Delay(1000 * (1 << t), cancellationToken).ConfigureAwait(false);
			}
		}

		cancellationToken.ThrowIfCancellationRequested();

		throw new InvalidOperationException("This should never happen");
	}

	private static Uri BuildUserCommentsUri(Uri uri, string? after, int pageLimit) {
		string query = $"sort=new&limit={pageLimit}";

		if (!string.IsNullOrWhiteSpace(after)) {
			query += "&after=" + Uri.EscapeDataString(after);
		}

		return new Uri($"{uri.ToString().TrimEnd('/')}/user/{RedditHelper.User}?{query}", UriKind.Absolute);
	}

	private static async Task<IReadOnlyCollection<RedditGameEntry>> MonitorDownloads(LinkedList<Task<IReadOnlyCollection<RedditGameEntry>>> tasks, CancellationToken cancellationToken) {
		while (tasks.Count > 0) {
			cancellationToken.ThrowIfCancellationRequested();

			await Task.WhenAny(tasks).ConfigureAwait(false);

			LinkedListNode<Task<IReadOnlyCollection<RedditGameEntry>>>? node = tasks.First;

			while (node is not null) {
				Task<IReadOnlyCollection<RedditGameEntry>> task = node.Value;

				if (task.IsCompletedSuccessfully) {
					IReadOnlyCollection<RedditGameEntry> result = await task.ConfigureAwait(false);

					if (result.Count > 0) {
						return result;
					}
				}

				if (task.IsCompleted) {
					tasks.Remove(node);
					node = tasks.First;

					continue;
				}

				node = node.Next;
			}
		}

		return [];
	}

	/// <summary>
	///     Shuffles a list of URIs. <br />
	///     This is done using a non performant guids generation for asf trimmed binary compatibility.
	/// </summary>
	/// <param name="list">The list of URIs to shuffle.</param>
	/// <returns>A shuffled list of URIs.</returns>
	private static List<Uri> Shuffle<TCollection>(TCollection list) where TCollection : ICollection<Uri> {
		List<(Guid, Uri)> randomized = new(list.Count);
		randomized.AddRange(list.Select(static uri => (Guid.NewGuid(), uri)));

		randomized.Sort(static (x, y) => x.Item1.CompareTo(y.Item1));

		return randomized.Select(static x => x.Item2).ToList();
	}

	private readonly record struct RedlibPageDownloadResult(IReadOnlyCollection<RedditGameEntry> Entries, string? After);
}
