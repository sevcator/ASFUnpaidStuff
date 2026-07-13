using System;

namespace Maxisoft.ASF.Freebies;

/// <summary>
/// Pure decision helpers for the freebie scanner, kept side-effect free so they stay unit-testable.
/// </summary>
internal static class FreebieLogic {
	/// <summary>
	/// A point-shop catalog entry is claimable for free when Steam prices it at exactly "0" points.
	/// </summary>
	public static bool IsFreeRewardItem(RewardItemData? item) => item is { DefId: not 0 } && string.Equals(item.PointCost, "0", StringComparison.Ordinal);

	public static bool ShouldAttemptClaim(DateTimeOffset nextAllowedClaim, DateTimeOffset now) => now >= nextAllowedClaim;

	/// <summary>
	/// Computes when the next sale-item claim attempt should happen, based on Steam's answer.
	/// Falls back to <paramref name="fallbackCooldown"/> when Steam did not provide a usable timestamp,
	/// so a bot without a running sale event is not hammered on every scan.
	/// </summary>
	public static DateTimeOffset NextSaleItemClaimTime(ClaimItemResponse? response, DateTimeOffset now, TimeSpan fallbackCooldown) {
		if (response?.NextClaimTime > 0) {
			DateTimeOffset next = DateTimeOffset.FromUnixTimeSeconds(response.NextClaimTime);

			if (next > now) {
				return next;
			}
		}

		return now + fallbackCooldown;
	}

	/// <summary>
	/// After a successful claim, decides whether Steam may still have more queued items to hand out:
	/// a missing or non-future next_claim_time means "claim again right away" (e.g. a bot that was
	/// offline for several event days), while a future timestamp means we are done for now.
	/// </summary>
	public static bool MoreSaleItemsMayBeQueued(ClaimItemResponse response, DateTimeOffset now) => (response.NextClaimTime <= 0) || (DateTimeOffset.FromUnixTimeSeconds(response.NextClaimTime) <= now);

	public static string DescribeReward(RewardItemData? item) {
		if (item is null) {
			return "unknown item";
		}

		return item.CommunityItem?.ItemName ?? item.CommunityItem?.ItemTitle ?? $"item defid {item.DefId}";
	}
}
