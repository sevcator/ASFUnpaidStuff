using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;
using ASFUnpaidStuff.ASFExtensions.Games;
using ASFUnpaidStuff.Configurations;
using Maxisoft.ASF.ASFExtensions.Games;
using Maxisoft.ASF.FreeGames;
using Maxisoft.ASF.HttpClientSimple;

#nullable enable

namespace Maxisoft.ASF.SteamPackages;

internal enum ESteamStoreProfileFeatureStatus : byte {
	Unknown,
	Supported,
	Limited
}

internal static class SteamStoreProfileFeatureValidator {
	private static readonly ConcurrentDictionary<uint, CachedStatus> AppStatusCache = new();

	public static async Task<SteamStoreProfileValidationResult> FilterCandidates(Bot productInfoBot, IReadOnlyCollection<GameIdentifier> candidates, ASFUnpaidStuffOptions options, SimpleHttpClient httpClient, CancellationToken cancellationToken = default) {
		ArgumentNullException.ThrowIfNull(productInfoBot);
		ArgumentNullException.ThrowIfNull(candidates);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(httpClient);

		if (!options.Sources.SteamStoreProfileCheck || (candidates.Count == 0)) {
			return new SteamStoreProfileValidationResult(candidates, [], []);
		}

		Dictionary<GameIdentifier, HashSet<uint>> appIdsByCandidate = await ResolveAppIds(productInfoBot, candidates, cancellationToken).ConfigureAwait(false);
		HashSet<uint> appIds = appIdsByCandidate.Values.SelectMany(static ids => ids).Where(static appId => appId > 0).ToHashSet();
		Dictionary<uint, ESteamStoreProfileFeatureStatus> statuses = await ResolveStoreStatuses(appIds, options.SteamStore, httpClient, cancellationToken).ConfigureAwait(false);

		List<GameIdentifier> accepted = [];
		List<GameIdentifier> rejectedLimited = [];
		List<GameIdentifier> rejectedUnknown = [];

		foreach (GameIdentifier candidate in candidates) {
			if (!appIdsByCandidate.TryGetValue(candidate, out HashSet<uint>? candidateAppIds) || (candidateAppIds.Count == 0)) {
				if (options.SteamStore.RejectUnknownProfileFeatures) {
					rejectedUnknown.Add(candidate);
				}
				else {
					accepted.Add(candidate);
				}

				continue;
			}

			bool hasLimited = false;
			bool hasUnknown = false;

			foreach (uint appId in candidateAppIds) {
				ESteamStoreProfileFeatureStatus status = statuses.GetValueOrDefault(appId, ESteamStoreProfileFeatureStatus.Unknown);
				hasLimited |= status is ESteamStoreProfileFeatureStatus.Limited;
				hasUnknown |= status is ESteamStoreProfileFeatureStatus.Unknown;
			}

			if (hasLimited && options.SteamStore.RejectProfileFeaturesLimited) {
				rejectedLimited.Add(candidate);
			}
			else if (hasUnknown && options.SteamStore.RejectUnknownProfileFeatures) {
				rejectedUnknown.Add(candidate);
			}
			else {
				accepted.Add(candidate);
			}
		}

		return new SteamStoreProfileValidationResult(accepted, rejectedLimited, rejectedUnknown);
	}

	internal static ESteamStoreProfileFeatureStatus InspectAppPage(string? html) {
		if (string.IsNullOrWhiteSpace(html)) {
			return ESteamStoreProfileFeatureStatus.Unknown;
		}

		if (html.Contains("Profile Features Limited", StringComparison.OrdinalIgnoreCase)) {
			return ESteamStoreProfileFeatureStatus.Limited;
		}

		if (html.Contains("Oops, sorry!", StringComparison.OrdinalIgnoreCase) ||
			html.Contains("This item is currently unavailable", StringComparison.OrdinalIgnoreCase) ||
			html.Contains("Sign in to view", StringComparison.OrdinalIgnoreCase) ||
			html.Contains("Access Denied", StringComparison.OrdinalIgnoreCase)) {
			return ESteamStoreProfileFeatureStatus.Unknown;
		}

		return ESteamStoreProfileFeatureStatus.Supported;
	}

	private static async Task<Dictionary<GameIdentifier, HashSet<uint>>> ResolveAppIds(Bot productInfoBot, IReadOnlyCollection<GameIdentifier> candidates, CancellationToken cancellationToken) {
		Dictionary<GameIdentifier, HashSet<uint>> res = new();
		HashSet<uint> packageIds = [];

		foreach (GameIdentifier candidate in candidates) {
			if (!candidate.Valid || (candidate.Id <= 0) || (candidate.Id > uint.MaxValue)) {
				res[candidate] = [];

				continue;
			}

			uint id = checked((uint) candidate.Id);

			switch (candidate.Type) {
				case GameIdentifierType.App:
					res[candidate] = [id];

					break;
				case GameIdentifierType.Sub:
					packageIds.Add(id);

					break;
				default:
					res[candidate] = [];

					break;
			}
		}

		if (packageIds.Count == 0) {
			return res;
		}

		SteamPackageInfoResult? productInfos = await SteamPackageProductInfoClient.GetPackageInfos(productInfoBot, packageIds, cancellationToken).ConfigureAwait(false);

		foreach (uint packageId in packageIds) {
			GameIdentifier candidate = new(packageId, GameIdentifierType.Sub);

			if (productInfos?.Packages.TryGetValue(packageId, out SteamPackageCandidateInfo? packageInfo) is true) {
				res[candidate] = packageInfo.PackageContentIds.ToHashSet();
			}
			else {
				res[candidate] = [];
			}
		}

		return res;
	}

	private static async Task<Dictionary<uint, ESteamStoreProfileFeatureStatus>> ResolveStoreStatuses(IReadOnlyCollection<uint> appIds, ASFUnpaidStuffSteamStoreOptions options, SimpleHttpClient httpClient, CancellationToken cancellationToken) {
		Dictionary<uint, ESteamStoreProfileFeatureStatus> res = new();

		if (appIds.Count == 0) {
			return res;
		}

		using SemaphoreSlim semaphore = new(Math.Max(1, options.MaxParallelRequests));

		Task<(uint appId, ESteamStoreProfileFeatureStatus status)>[] tasks = appIds
			.Select(appId => ResolveStoreStatus(appId, options, httpClient, semaphore, cancellationToken))
			.ToArray();

		foreach ((uint appId, ESteamStoreProfileFeatureStatus status) in await Task.WhenAll(tasks).ConfigureAwait(false)) {
			res[appId] = status;
		}

		return res;
	}

	private static async Task<(uint appId, ESteamStoreProfileFeatureStatus status)> ResolveStoreStatus(uint appId, ASFUnpaidStuffSteamStoreOptions options, SimpleHttpClient httpClient, SemaphoreSlim semaphore, CancellationToken cancellationToken) {
		DateTimeOffset now = DateTimeOffset.UtcNow;

		if ((options.CacheTtl > TimeSpan.Zero) && AppStatusCache.TryGetValue(appId, out CachedStatus cachedStatus) && (cachedStatus.ValidUntil > now)) {
			return (appId, cachedStatus.Status);
		}

		await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			ESteamStoreProfileFeatureStatus status = await FetchStoreStatus(appId, httpClient, cancellationToken).ConfigureAwait(false);
			AppStatusCache[appId] = new CachedStatus(status, now.Add(options.CacheTtl));

			return (appId, status);
		}
		catch (Exception e) when (e is HttpRequestException or IOException) {
			// Transient network failure: surface Unknown for this run but do not cache it, so the app is re-checked
			// next run instead of being rejected for the whole (default 7-day) cache window over one failed request.
			return (appId, ESteamStoreProfileFeatureStatus.Unknown);
		}
		finally {
			semaphore.Release();
		}
	}

	private static async Task<ESteamStoreProfileFeatureStatus> FetchStoreStatus(uint appId, SimpleHttpClient httpClient, CancellationToken cancellationToken) {
		Uri uri = new($"https://store.steampowered.com/app/{appId}/?l=english", UriKind.Absolute);

		try {
#pragma warning disable CAC001
#pragma warning disable CA2007
			await using HttpStreamResponse response = await httpClient.GetStreamAsync(uri, cancellationToken: cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2007
#pragma warning restore CAC001

			if (!response.Response.IsSuccessStatusCode || !response.HasValidStream) {
				return ESteamStoreProfileFeatureStatus.Unknown;
			}

			string html = await response.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

			return InspectAppPage(html);
		}
		catch (Exception e) when (e is InvalidOperationException or TimeoutException) {
			return ESteamStoreProfileFeatureStatus.Unknown;
		}
	}

	private readonly record struct CachedStatus(ESteamStoreProfileFeatureStatus Status, DateTimeOffset ValidUntil);
}

internal readonly record struct SteamStoreProfileValidationResult(IReadOnlyCollection<GameIdentifier> AcceptedCandidates, IReadOnlyCollection<GameIdentifier> RejectedLimitedCandidates, IReadOnlyCollection<GameIdentifier> RejectedUnknownCandidates);
