using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;
using ASFUnpaidStuff.ASFExtensions.Games;
using Maxisoft.ASF.AppLists;
using Maxisoft.ASF.ASFExtensions.Games;
using Maxisoft.ASF.FreeGames;
using SteamKit2;

#nullable enable

namespace Maxisoft.ASF.SteamPackages;

internal static class SteamPackageDiscoveryService {
	private static readonly SteamPackageState State = SteamPackageState.CreateDefault();
	private static readonly SemaphoreSlim PICSChangesSemaphore = new(1, 1);
	private static readonly TimeSpan PICSChangesLimitingDelay = TimeSpan.FromSeconds(10);

	private static void DebugLog(string message, string callerName) => UnpaidStuffDebugLogger.Log(bot: null, message, callerName);

	public static async Task<uint> GetPreferredChangeNumberToStartFrom(CancellationToken cancellationToken = default) {
		uint lastChangeNumber = await State.GetLastChangeNumber(cancellationToken).ConfigureAwait(false);

		if (lastChangeNumber > 0) {
			DebugLog($"preferred Steam PICS change number from ASFUnpaidStuff state: {lastChangeNumber}", nameof(GetPreferredChangeNumberToStartFrom));

			return lastChangeNumber;
		}

		uint freePackagesLastChangeNumber = await TryReadFreePackagesLastChangeNumber(cancellationToken).ConfigureAwait(false);
		DebugLog($"preferred Steam PICS change number from FreePackages cache fallback: {freePackagesLastChangeNumber}", nameof(GetPreferredChangeNumberToStartFrom));

		return freePackagesLastChangeNumber;
	}

	public static async Task OnPICSChanges(uint currentChangeNumber, IReadOnlyDictionary<uint, SteamApps.PICSChangesCallback.PICSChangeData> packageChanges, CancellationToken cancellationToken = default) {
		DebugLog($"received Steam PICS changes: currentChangeNumber={currentChangeNumber}, packageChanges={packageChanges.Count}", nameof(OnPICSChanges));

		if (packageChanges.Count == 0) {
			await State.UpdateLastChangeNumber(currentChangeNumber, cancellationToken).ConfigureAwait(false);

			return;
		}

		await State.AddChanges(currentChangeNumber, packageChanges.Keys, cancellationToken).ConfigureAwait(false);
	}

	public static async Task OnPICSChangesRestart(Bot? refreshBot, uint currentChangeNumber, CancellationToken cancellationToken = default) {
		if (refreshBot is null) {
			DebugLog($"Steam PICS restart at {currentChangeNumber}, but no refresh bot is available", nameof(OnPICSChangesRestart));

			return;
		}

		DebugLog($"Steam PICS restart at {currentChangeNumber}; refreshing with {refreshBot.BotName}", nameof(OnPICSChangesRestart));

		await RefreshFromSteam(refreshBot, currentChangeNumber, cancellationToken).ConfigureAwait(false);
	}

	public static async Task<SteamPackageDiscoveryResult> GetCandidates(Bot refreshBot, SteamPackageCheckedDatabase checkedDatabase, IReadOnlySet<uint>? validatedPackageIds = null, IReadOnlySet<uint>? checkedPackageSnapshot = null, CancellationToken cancellationToken = default) {
		Stopwatch stopwatch = Stopwatch.StartNew();
		await RefreshFromSteam(refreshBot, currentChangeNumberHint: null, cancellationToken).ConfigureAwait(false);

		HashSet<uint> pendingPackages = await State.GetPendingPackages(cancellationToken).ConfigureAwait(false);
		DebugLog($"Steam package discovery loaded {pendingPackages.Count} pending package(s)", nameof(GetCandidates));

		if (pendingPackages.Count == 0) {
			return SteamPackageDiscoveryResult.Empty;
		}

		IReadOnlySet<uint> checkedPackageSet = checkedPackageSnapshot ?? (await checkedDatabase.Snapshot(cancellationToken).ConfigureAwait(false)).ToHashSet();
		SteamPackagePreFilterResult preFilterResult = SteamPackagePreFilter.Apply(pendingPackages, checkedPackageSet, validatedPackageIds);
		List<uint> checkedPackages = preFilterResult.CheckedSkippedPackages.ToList();
		List<uint> validatedPackages = preFilterResult.ValidatedSkippedPackages.ToList();
		List<uint> knownPackagesToRemove = checkedPackages.Concat(validatedPackages).Distinct().ToList();

		DebugLog($"Steam checked DB snapshot has {checkedPackageSet.Count} package(s); {checkedPackages.Count} pending package(s) can skip probe; {validatedPackages.Count} pending package(s) already validated", nameof(GetCandidates));

		if (knownPackagesToRemove.Count > 0) {
			await State.RemovePending(knownPackagesToRemove, cancellationToken).ConfigureAwait(false);
		}

		pendingPackages = preFilterResult.RemainingPackages;
		GameIdentifier[] validatedCandidates = preFilterResult.ValidatedCandidates.ToArray();

		if (pendingPackages.Count == 0) {
			DebugLog($"Steam package discovery finished after known-ID skip only in {stopwatch.ElapsedMilliseconds} ms; validatedCandidates={validatedCandidates.Length}", nameof(GetCandidates));

			return new SteamPackageDiscoveryResult(validatedCandidates, NewCandidateCount: 0, SkippedCheckedCount: checkedPackages.Count, KnownValidatedCount: validatedCandidates.Length);
		}

		SteamPackageFilterResult? filterResult = await SteamPackageProductInfoClient.FilterFreePackageIds(refreshBot, pendingPackages, cancellationToken).ConfigureAwait(false);

		if (filterResult is null) {
			DebugLog($"Steam package discovery did not get product info; {pendingPackages.Count} package(s) remain pending", nameof(GetCandidates));

			return new SteamPackageDiscoveryResult(validatedCandidates, NewCandidateCount: 0, SkippedCheckedCount: checkedPackages.Count, KnownValidatedCount: validatedCandidates.Length);
		}

		if (filterResult.Value.RejectedPackageIds.Count > 0) {
			await State.RemovePending(filterResult.Value.RejectedPackageIds, cancellationToken).ConfigureAwait(false);
		}

		GameIdentifier[] newCandidates = filterResult.Value.AcceptedPackageIds
			.Select(static packageId => new GameIdentifier(packageId, GameIdentifierType.Sub))
			.ToArray();
		GameIdentifier[] candidates = validatedCandidates.Concat(newCandidates).ToArray();

		DebugLog($"Steam package discovery finished in {stopwatch.ElapsedMilliseconds} ms: {newCandidates.Length} new candidate(s), {validatedCandidates.Length} already validated candidate(s), {filterResult.Value.RejectedPackageIds.Count} rejected/unknown, {checkedPackages.Count} checked-skip", nameof(GetCandidates));

		return new SteamPackageDiscoveryResult(candidates, newCandidates.Length, checkedPackages.Count, validatedCandidates.Length);
	}

	public static async Task RemovePending(uint packageId, CancellationToken cancellationToken = default) {
		if (packageId == 0) {
			return;
		}

		await State.RemovePending([packageId], cancellationToken).ConfigureAwait(false);
	}

	public static async Task RemovePending(IEnumerable<uint> packageIds, CancellationToken cancellationToken = default) {
		ArgumentNullException.ThrowIfNull(packageIds);

		uint[] normalizedPackageIds = packageIds.Where(static packageId => packageId > 0).Distinct().ToArray();

		if (normalizedPackageIds.Length == 0) {
			return;
		}

		await State.RemovePending(normalizedPackageIds, cancellationToken).ConfigureAwait(false);
	}

	private static async Task RefreshFromSteam(Bot refreshBot, uint? currentChangeNumberHint, CancellationToken cancellationToken) {
		uint lastChangeNumber = await State.GetLastChangeNumber(cancellationToken).ConfigureAwait(false);

		if (lastChangeNumber == 0) {
			lastChangeNumber = await TryReadFreePackagesLastChangeNumber(cancellationToken).ConfigureAwait(false);

			if (lastChangeNumber > 0) {
				await State.UpdateLastChangeNumber(lastChangeNumber, cancellationToken).ConfigureAwait(false);
				DebugLog($"seeded Steam PICS state from FreePackages cache at change #{lastChangeNumber}", nameof(RefreshFromSteam));
			}
		}

		if (lastChangeNumber == 0) {
			DebugLog("Steam PICS refresh skipped because no change number is known yet", nameof(RefreshFromSteam));

			return;
		}

		DebugLog($"refreshing Steam PICS package changes since #{lastChangeNumber} using {refreshBot.BotName}", nameof(RefreshFromSteam));

		SteamApps.PICSChangesCallback? picsChanges = await FetchPICSChanges(refreshBot, lastChangeNumber, cancellationToken).ConfigureAwait(false);

		if (picsChanges is null) {
			DebugLog("Steam PICS refresh returned no data; keeping pending state unchanged", nameof(RefreshFromSteam));

			return;
		}

		DebugLog($"Steam PICS refresh result: current=#{picsChanges.CurrentChangeNumber}, packageChanges={picsChanges.PackageChanges.Count}, requiresFullPackageUpdate={picsChanges.RequiresFullPackageUpdate}", nameof(RefreshFromSteam));

		if (!picsChanges.RequiresFullPackageUpdate) {
			await OnPICSChanges(picsChanges.CurrentChangeNumber, picsChanges.PackageChanges, cancellationToken).ConfigureAwait(false);

			return;
		}

		if (currentChangeNumberHint is not null && currentChangeNumberHint.Value > lastChangeNumber) {
			await State.UpdateLastChangeNumber(currentChangeNumberHint.Value, cancellationToken).ConfigureAwait(false);
			DebugLog($"Steam PICS full package update requested; advanced state to restart hint #{currentChangeNumberHint.Value}", nameof(RefreshFromSteam));
		}
	}

	private static async Task<SteamApps.PICSChangesCallback?> FetchPICSChanges(Bot refreshBot, uint changeNumber, CancellationToken cancellationToken) {
		await PICSChangesSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			if (!refreshBot.IsConnectedAndLoggedOn) {
				DebugLog($"cannot fetch Steam PICS changes: refresh bot {refreshBot.BotName} is not logged in", nameof(FetchPICSChanges));

				return null;
			}

			return await refreshBot.SteamApps.PICSGetChangesSince(changeNumber, false, true).ToLongRunningTask().WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception e) when (e is TimeoutException or InvalidOperationException or SteamKit2.AsyncJobFailedException) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarning($"[UnpaidStuff] Unable to fetch Steam PICS changes: {e.Message}");

			return null;
		}
		finally {
			_ = Task.Run(async () => {
				await Task.Delay(PICSChangesLimitingDelay).ConfigureAwait(false);
				PICSChangesSemaphore.Release();
			}, CancellationToken.None);
		}
	}

	private static async Task<uint> TryReadFreePackagesLastChangeNumber(CancellationToken cancellationToken) {
		string filePath = Path.Combine(ArchiSteamFarm.SharedInfo.ConfigDirectory, "FreePackages.cache");

		if (!File.Exists(filePath)) {
			return 0;
		}

		try {
			string json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
			using JsonDocument document = JsonDocument.Parse(json);

			return document.RootElement.TryGetProperty("LastChangeNumber", out JsonElement element) && element.TryGetUInt32(out uint lastChangeNumber) ? lastChangeNumber : 0;
		}
		catch (Exception) {
			return 0;
		}
	}
}

internal readonly record struct SteamPackageDiscoveryResult(IReadOnlyCollection<GameIdentifier> Candidates, int NewCandidateCount, int SkippedCheckedCount, int KnownValidatedCount) {
	public static SteamPackageDiscoveryResult Empty { get; } = new([], 0, 0, 0);
}
