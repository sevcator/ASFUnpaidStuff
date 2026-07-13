using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;
using ASFUnpaidStuff.Configurations;

namespace Maxisoft.ASF.Freebies;

/// <summary>
/// Scans bots for free stuff that Steam hands out during sales and events:
/// the daily sale-event item (stickers/cards), discovery-queue exploration rewards,
/// and zero-cost point-shop giveaways. Keeps per-bot cooldown state in memory so the
/// scheduled scans stay cheap; a manual command run bypasses the cooldowns.
/// </summary>
internal sealed class FreebieService : IDisposable {
	// How long to wait before retrying a sale-item claim when Steam gave no usable next_claim_time
	private static readonly TimeSpan SaleItemFallbackCooldown = TimeSpan.FromHours(6);

	// Queue exploration and point-shop rescans are daily-ish activities; 20h keeps them aligned with Steam's daily resets while tolerating scan-time drift
	private static readonly TimeSpan QueueExplorationCooldown = TimeSpan.FromHours(20);
	private static readonly TimeSpan PointShopScanCooldown = TimeSpan.FromHours(20);

	private static readonly TimeSpan DelayBetweenQueueItems = TimeSpan.FromMilliseconds(300);
	private static readonly TimeSpan DelayBetweenClaims = TimeSpan.FromMilliseconds(500);
	private static readonly TimeSpan DelayBetweenBots = TimeSpan.FromSeconds(3);

	private sealed class BotFreebieState {
		public DateTimeOffset NextSaleItemClaim = DateTimeOffset.MinValue;
		public DateTimeOffset NextQueueExploration = DateTimeOffset.MinValue;
		public DateTimeOffset NextPointShopScan = DateTimeOffset.MinValue;
		public readonly HashSet<uint> RedeemedDefIds = [];
	}

	internal readonly record struct FreebieScanSummary(int BotsScanned, int SaleItemsClaimed, int PointShopItemsClaimed, int QueuePassesExplored) {
		public override string ToString() => $"scanned {BotsScanned} bot(s): claimed {SaleItemsClaimed} sale event item(s), {PointShopItemsClaimed} free point-shop item(s), explored {QueuePassesExplored} discovery queue(s)";
	}

	private readonly ConcurrentDictionary<string, BotFreebieState> States = new(StringComparer.OrdinalIgnoreCase);

	// Serializes scans: the scheduled tick and a manual FREEBIES command share this instance and would otherwise race on the per-bot state
	private readonly SemaphoreSlim ScanLock = new(1, 1);

	public void Dispose() => ScanLock.Dispose();

	public async Task<FreebieScanSummary> Scan(IReadOnlyCollection<Bot> bots, ASFUnpaidStuffOptions options, bool ignoreCooldowns, CancellationToken cancellationToken) {
		if (!await ScanLock.WaitAsync(100, cancellationToken).ConfigureAwait(false)) {
			// Another scan is already running; a second concurrent pass would only duplicate requests
			return default(FreebieScanSummary);
		}

		try {
			return await ScanCore(bots, options, ignoreCooldowns, cancellationToken).ConfigureAwait(false);
		}
		finally {
			ScanLock.Release();
		}
	}

	private async Task<FreebieScanSummary> ScanCore(IReadOnlyCollection<Bot> bots, ASFUnpaidStuffOptions options, bool ignoreCooldowns, CancellationToken cancellationToken) {
		ASFUnpaidStuffFreebieOptions freebieOptions = options.Freebies;
		int botsScanned = 0, saleItems = 0, pointShopItems = 0, queuePasses = 0;
		bool first = true;

		foreach (Bot bot in bots) {
			cancellationToken.ThrowIfCancellationRequested();

			if (!bot.IsConnectedAndLoggedOn || string.IsNullOrEmpty(bot.AccessToken) || options.IsBlacklisted(bot)) {
				continue;
			}

			if (!first) {
				await Task.Delay(DelayBetweenBots, cancellationToken).ConfigureAwait(false);
			}

			first = false;
			botsScanned++;
			BotFreebieState state = States.GetOrAdd(bot.BotName, static _ => new BotFreebieState());
			DateTimeOffset now = DateTimeOffset.UtcNow;

			if (freebieOptions.ClaimSaleEventItems && (ignoreCooldowns || FreebieLogic.ShouldAttemptClaim(state.NextSaleItemClaim, now))) {
				saleItems += await ClaimSaleItems(bot, state, cancellationToken).ConfigureAwait(false);
			}

			if (freebieOptions.ClaimFreePointShopItems && (ignoreCooldowns || FreebieLogic.ShouldAttemptClaim(state.NextPointShopScan, now))) {
				pointShopItems += await ClaimFreePointShopItems(bot, state, cancellationToken).ConfigureAwait(false);
			}

			if (freebieOptions.ExploreDiscoveryQueue && (ignoreCooldowns || FreebieLogic.ShouldAttemptClaim(state.NextQueueExploration, now))) {
				queuePasses += await ExploreDiscoveryQueue(bot, state, freebieOptions.DiscoveryQueuePasses, cancellationToken).ConfigureAwait(false);
			}
		}

		return new FreebieScanSummary(botsScanned, saleItems, pointShopItems, queuePasses);
	}

	private static async Task<int> ClaimSaleItems(Bot bot, BotFreebieState state, CancellationToken cancellationToken) {
		int claimed = 0;

		try {
			// A sale event can queue up more than one claimable item (e.g. after days offline); drain with a small safety bound
			for (int attempt = 0; attempt < 3; attempt++) {
				ClaimItemResponse? response = await FreebieWebClient.ClaimSaleItem(bot, cancellationToken).ConfigureAwait(false);
				DateTimeOffset now = DateTimeOffset.UtcNow;
				state.NextSaleItemClaim = FreebieLogic.NextSaleItemClaimTime(response, now, SaleItemFallbackCooldown);

				if (response?.RewardItem is null) {
					if (claimed == 0) {
						bot.ArchiLogger.LogGenericDebug("Freebies: no sale event item to claim right now");
					}

					break;
				}

				claimed++;
				bot.ArchiLogger.LogGenericInfo($"Freebies: claimed sale event item \"{FreebieLogic.DescribeReward(response.RewardItem)}\"");

				// Decide from Steam's raw answer, not the clamped cooldown state: a missing/non-future
				// next_claim_time after a successful claim means another item is immediately claimable
				if (!FreebieLogic.MoreSaleItemsMayBeQueued(response, now)) {
					break;
				}

				await Task.Delay(DelayBetweenClaims, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) {
			throw;
		}
		catch (Exception ex) {
			bot.ArchiLogger.LogGenericDebug($"Freebies: sale item claim failed: {ex.Message}");
		}

		return claimed;
	}

	private static async Task<int> ClaimFreePointShopItems(Bot bot, BotFreebieState state, CancellationToken cancellationToken) {
		int claimed = 0;

		try {
			QueryRewardItemsResponse? response = await FreebieWebClient.QueryRewardItems(bot, cancellationToken).ConfigureAwait(false);
			state.NextPointShopScan = DateTimeOffset.UtcNow + PointShopScanCooldown;

			if (response?.Definitions is not { Count: > 0 } definitions) {
				return 0;
			}

			foreach (RewardItemData item in definitions) {
				cancellationToken.ThrowIfCancellationRequested();

				if (!FreebieLogic.IsFreeRewardItem(item) || state.RedeemedDefIds.Contains(item.DefId)) {
					continue;
				}

				bool? success = await FreebieWebClient.RedeemPoints(bot, item.DefId, cancellationToken).ConfigureAwait(false);

				if (success is null) {
					// Bot lost its session mid-scan — nothing was attempted; leave items unmarked so the next scan retries them
					break;
				}

				// Remember an actual attempt either way: a failed redeem is almost always "already owned", retrying every day would only spam Steam
				state.RedeemedDefIds.Add(item.DefId);

				if (success is true) {
					claimed++;
					bot.ArchiLogger.LogGenericInfo($"Freebies: redeemed free point-shop item \"{FreebieLogic.DescribeReward(item)}\"");
				}

				await Task.Delay(DelayBetweenClaims, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) {
			throw;
		}
		catch (Exception ex) {
			bot.ArchiLogger.LogGenericDebug($"Freebies: point-shop scan failed: {ex.Message}");
		}

		return claimed;
	}

	private static async Task<int> ExploreDiscoveryQueue(Bot bot, BotFreebieState state, int passes, CancellationToken cancellationToken) {
		int completedPasses = 0;

		try {
			for (int pass = 0; pass < passes; pass++) {
				GetDiscoveryQueueResponse? queue = await FreebieWebClient.GetDiscoveryQueue(bot, cancellationToken).ConfigureAwait(false);

				if (queue?.AppIds is not { Count: > 0 } appIds) {
					break;
				}

				bool passCleared = true;

				foreach (uint appId in appIds) {
					cancellationToken.ThrowIfCancellationRequested();

					if (!await FreebieWebClient.SkipDiscoveryQueueItem(bot, appId, cancellationToken).ConfigureAwait(false)) {
						passCleared = false;

						break;
					}

					await Task.Delay(DelayBetweenQueueItems, cancellationToken).ConfigureAwait(false);
				}

				if (!passCleared) {
					// Session dropped or Steam refused mid-queue: don't count the pass and don't set the
					// daily cooldown below, so the next scheduled scan retries the queue
					bot.ArchiLogger.LogGenericDebug("Freebies: discovery queue pass aborted, will retry on the next scan");

					break;
				}

				completedPasses++;
			}

			if (completedPasses > 0) {
				state.NextQueueExploration = DateTimeOffset.UtcNow + QueueExplorationCooldown;
				bot.ArchiLogger.LogGenericInfo($"Freebies: explored the discovery queue {completedPasses} time(s)");
			}
		}
		catch (OperationCanceledException) {
			throw;
		}
		catch (Exception ex) {
			bot.ArchiLogger.LogGenericDebug($"Freebies: discovery queue exploration failed: {ex.Message}");
		}

		return completedPasses;
	}
}
