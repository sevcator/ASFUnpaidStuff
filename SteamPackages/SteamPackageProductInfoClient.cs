using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;
using Maxisoft.ASF.FreeGames;
using SteamKit2;

#nullable enable

namespace Maxisoft.ASF.SteamPackages;

internal static class SteamPackageProductInfoClient {
	private const int ItemsPerProductInfoRequest = 255;
	private static readonly TimeSpan ProductInfoLimitingDelay = TimeSpan.FromSeconds(10);
	private static readonly SemaphoreSlim ProductInfoSemaphore = new(1, 1);

	public static async Task<SteamPackageFilterResult?> FilterFreePackageIds(Bot bot, IReadOnlyCollection<uint> packageIds, CancellationToken cancellationToken = default) {
		if (packageIds.Count == 0) {
			return SteamPackageFilterResult.Empty;
		}

		DebugLog($"filtering {packageIds.Count} Steam package ID(s) through PICS product info");

		SteamPackageInfoResult? packageInfos = await GetPackageInfos(bot, packageIds, cancellationToken).ConfigureAwait(false);

		if (packageInfos is null) {
			return null;
		}

		HashSet<uint> accepted = [];
		HashSet<uint> rejected = packageInfos.Value.UnknownPackageIds.ToHashSet();

		foreach (SteamPackageCandidateInfo package in packageInfos.Value.Packages.Values) {
			if (package.IsFreeAvailablePackage(DateTimeOffset.UtcNow)) {
				accepted.Add(package.Id);
			}
			else {
				rejected.Add(package.Id);
			}
		}

		rejected.ExceptWith(accepted);

		DebugLog($"PICS product info final filter: {accepted.Count} accepted, {rejected.Count} rejected/unknown");

		return new SteamPackageFilterResult(accepted, rejected);
	}

	public static async Task<SteamPackageInfoResult?> GetPackageInfos(Bot bot, IReadOnlyCollection<uint> packageIds, CancellationToken cancellationToken = default) {
		if (packageIds.Count == 0) {
			return SteamPackageInfoResult.Empty;
		}

		Dictionary<uint, SteamPackageCandidateInfo> packages = new();
		HashSet<uint> unknown = [];
		int batchIndex = 0;

		foreach (HashSet<uint> batch in GetBatches(packageIds)) {
			cancellationToken.ThrowIfCancellationRequested();
			batchIndex++;

			DebugLog($"PICS product info batch #{batchIndex}: {batch.Count} package(s)");

			List<SteamApps.PICSProductInfoCallback>? productInfos = await FetchProductInfo(bot, packageIds: batch, cancellationToken).ConfigureAwait(false);

			if (productInfos is null) {
				DebugLog($"PICS product info batch #{batchIndex} failed; keeping packages pending");

				return null;
			}

			HashSet<uint> seenPackageIds = productInfos.SelectMany(static result => result.Packages.Keys).ToHashSet();
			HashSet<uint> batchUnknown = productInfos.SelectMany(static result => result.UnknownPackages).ToHashSet();
			batchUnknown.UnionWith(batch.Except(seenPackageIds));
			unknown.UnionWith(batchUnknown);

			int packageCountBefore = packages.Count;

			foreach (SteamPackageCandidateInfo package in productInfos
				.SelectMany(static result => result.Packages.Values)
				.Select(SteamPackageCandidateInfo.FromProductInfo)) {
				packages[package.Id] = package;
			}

			DebugLog($"PICS product info batch #{batchIndex} result: {packages.Count - packageCountBefore} package info(s), {batchUnknown.Count} unknown/missing");
		}

		return new SteamPackageInfoResult(packages, unknown);
	}

	private static IEnumerable<HashSet<uint>> GetBatches(IReadOnlyCollection<uint> packageIds) {
		uint[] ids = packageIds.Where(static packageId => packageId > 0).Distinct().ToArray();

		for (int i = 0; i < ids.Length; i += ItemsPerProductInfoRequest) {
			HashSet<uint> batch = new();
			int end = Math.Min(i + ItemsPerProductInfoRequest, ids.Length);

			for (int j = i; j < end; j++) {
				batch.Add(ids[j]);
			}

			yield return batch;
		}
	}

	private static async Task<List<SteamApps.PICSProductInfoCallback>?> FetchProductInfo(Bot bot, IEnumerable<uint>? packageIds = null, CancellationToken cancellationToken = default) {
		await ProductInfoSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			if (!bot.IsConnectedAndLoggedOn) {
				DebugLog($"cannot fetch PICS product info: refresh bot {bot.BotName} is not logged in");

				return null;
			}

			IEnumerable<SteamApps.PICSRequest> packages = packageIds is null
				? Enumerable.Empty<SteamApps.PICSRequest>()
				: packageIds.Select(static packageId => new SteamApps.PICSRequest(packageId, ArchiSteamFarm.Core.ASF.GlobalDatabase?.PackageAccessTokensReadOnly.GetValueOrDefault(packageId, 0UL) ?? 0UL));

			var response = await bot.SteamApps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), packages).ToLongRunningTask().WaitAsync(cancellationToken).ConfigureAwait(false);

			return response.Results?.ToList();
		}
		catch (OperationCanceledException) {
			throw;
		}
		catch (Exception e) when (e is TimeoutException or InvalidOperationException or SteamKit2.AsyncJobFailedException) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarning($"[UnpaidStuff] Unable to fetch Steam package product info: {e.Message}");

			return null;
		}
		finally {
			_ = Task.Run(async () => {
				await Task.Delay(ProductInfoLimitingDelay).ConfigureAwait(false);
				ProductInfoSemaphore.Release();
			}, CancellationToken.None);
		}
	}

	private static void DebugLog(string message) => UnpaidStuffDebugLogger.Log(bot: null, message, nameof(FilterFreePackageIds));
}

internal readonly record struct SteamPackageFilterResult(IReadOnlyCollection<uint> AcceptedPackageIds, IReadOnlyCollection<uint> RejectedPackageIds) {
	public static SteamPackageFilterResult Empty { get; } = new([], []);
}

internal readonly record struct SteamPackageInfoResult(IReadOnlyDictionary<uint, SteamPackageCandidateInfo> Packages, IReadOnlyCollection<uint> UnknownPackageIds) {
	public static SteamPackageInfoResult Empty { get; } = new(new Dictionary<uint, SteamPackageCandidateInfo>(), []);
}
