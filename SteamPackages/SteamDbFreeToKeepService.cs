using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm;
using ASFUnpaidStuff.Configurations;
using Maxisoft.ASF.FreeGames;
using Maxisoft.ASF.HttpClientSimple;

#nullable enable

namespace Maxisoft.ASF.SteamPackages;

internal static class SteamDbFreeToKeepService {
	private static readonly SemaphoreSlim CacheSemaphore = new(1, 1);
	private static string? CachedKey;
	private static DateTimeOffset CachedUntil;
	private static IReadOnlyCollection<uint> CachedPackageIds = [];

	public static async Task<IReadOnlyCollection<uint>> GetPackageIds(ASFUnpaidStuffOptions options, SimpleHttpClient httpClient, CancellationToken cancellationToken = default) {
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(httpClient);

		if (!options.Sources.SteamDbFreeToKeep) {
			return [];
		}

		string cacheKey = BuildCacheKey(options.SteamDb);
		DateTimeOffset now = DateTimeOffset.UtcNow;

		await CacheSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			if ((options.SteamDb.CacheTtl > TimeSpan.Zero) && (CachedKey == cacheKey) && (CachedUntil > now)) {
				return CachedPackageIds;
			}
		}
		finally {
			CacheSemaphore.Release();
		}

		HashSet<uint> packageIds = [];

		foreach (string importPath in options.SteamDb.ImportPaths) {
			cancellationToken.ThrowIfCancellationRequested();
			await AddFromImportPath(importPath, options, packageIds, cancellationToken).ConfigureAwait(false);
		}

		bool liveFetchFailed = false;

		if (!string.IsNullOrWhiteSpace(options.SteamDb.Url) && (packageIds.Count < options.SteamDb.MaxPackages)) {
			liveFetchFailed = !await AddFromLiveUrl(options, httpClient, packageIds, cancellationToken).ConfigureAwait(false);
		}

		uint[] snapshot = packageIds.Take(options.SteamDb.MaxPackages).ToArray();

		// Avoid poisoning the cache (default 12h) with a partial/empty result when the live SteamDB fetch failed:
		// keep returning the best-effort snapshot for this run, but let the next run retry instead of serving stale data.
		if (!liveFetchFailed) {
			await CacheSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

			try {
				CachedKey = cacheKey;
				CachedUntil = now.Add(options.SteamDb.CacheTtl);
				CachedPackageIds = snapshot;
			}
			finally {
				CacheSemaphore.Release();
			}
		}

		return snapshot;
	}

	private static async Task AddFromImportPath(string importPath, ASFUnpaidStuffOptions options, ISet<uint> packageIds, CancellationToken cancellationToken) {
		string resolvedPath = ResolveImportPath(importPath);

		if (!File.Exists(resolvedPath)) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarning($"[UnpaidStuff] SteamDB import file not found: {resolvedPath}");

			return;
		}

		try {
			string content = await File.ReadAllTextAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
			AddParsedPackageIds(content, options, packageIds);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarning($"[UnpaidStuff] Unable to read SteamDB import file {resolvedPath}: {e.Message}");
		}
	}

	/// <returns><c>true</c> when a response was obtained (even if it contained no packages); <c>false</c> on a transient failure that should be retried next run.</returns>
	private static async Task<bool> AddFromLiveUrl(ASFUnpaidStuffOptions options, SimpleHttpClient httpClient, ISet<uint> packageIds, CancellationToken cancellationToken) {
		if (!Uri.TryCreate(options.SteamDb.Url, UriKind.Absolute, out Uri? uri)) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarning($"[UnpaidStuff] Invalid SteamDB URL: {options.SteamDb.Url}");

			return true; // misconfiguration, not a transient error: retrying every run would not help
		}

		try {
#pragma warning disable CAC001
#pragma warning disable CA2007
			await using HttpStreamResponse response = await httpClient.GetStreamAsync(uri, cancellationToken: cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2007
#pragma warning restore CAC001

			if (!response.Response.IsSuccessStatusCode || !response.HasValidStream) {
				ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarning($"[UnpaidStuff] SteamDB source skipped: {uri} returned {response.StatusCode}");

				return false;
			}

			string content = await response.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			int before = packageIds.Count;
			AddParsedPackageIds(content, options, packageIds);

			if ((packageIds.Count == before) && LooksLikeBlockedSteamDbPage(content)) {
				ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarning("[UnpaidStuff] SteamDB source returned a locked/login/challenge page; use steamDb.importPaths with a saved export if needed");
			}

			return true;
		}
		catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or TimeoutException) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarning($"[UnpaidStuff] Unable to fetch SteamDB free packages: {e.Message}");

			return false;
		}
	}

	private static void AddParsedPackageIds(string content, ASFUnpaidStuffOptions options, ISet<uint> packageIds) {
		foreach (uint packageId in SteamDbFreeToKeepParser.ParsePackageIds(content, options.SteamDb.RequireDiscountPercent, options.SteamDb.MaxPackages - packageIds.Count)) {
			packageIds.Add(packageId);

			if (packageIds.Count >= options.SteamDb.MaxPackages) {
				break;
			}
		}
	}

	private static string ResolveImportPath(string importPath) {
		if (Path.IsPathFullyQualified(importPath)) {
			return importPath;
		}

		return Path.Combine(SharedInfo.ConfigDirectory, importPath);
	}

	private static string BuildCacheKey(ASFUnpaidStuffSteamDbOptions options) => string.Join('\n', [
		options.Url ?? "",
		options.RequireDiscountPercent.ToString(System.Globalization.CultureInfo.InvariantCulture),
		options.MaxPackages.ToString(System.Globalization.CultureInfo.InvariantCulture),
		string.Join('\n', options.ImportPaths.Order(StringComparer.OrdinalIgnoreCase))
	]);

	private static bool LooksLikeBlockedSteamDbPage(string content) =>
		content.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
		content.Contains("login", StringComparison.OrdinalIgnoreCase) ||
		content.Contains("challenge", StringComparison.OrdinalIgnoreCase) ||
		content.Contains("cloudflare", StringComparison.OrdinalIgnoreCase);
}
