using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Maxisoft.ASF.Freebies;

/// <summary>
/// Standard envelope wrapping every api.steampowered.com web-api answer: <c>{"response": {...}}</c>.
/// </summary>
internal sealed record SteamApiResponse<T> where T : class {
	[JsonPropertyName("response")]
	public T? Response { get; init; }
}

/// <summary>
/// Answer of ISaleItemRewardsService/ClaimItem — the daily free sale-event item (sticker, card, badge piece...).
/// A missing <see cref="RewardItem"/> means there is currently nothing to claim (no running event, or already claimed).
/// </summary>
internal sealed record ClaimItemResponse {
	[JsonPropertyName("communityitemid")]
	public string? CommunityItemId { get; init; }

	/// <summary>Unix timestamp (seconds) of the next allowed claim.</summary>
	[JsonPropertyName("next_claim_time")]
	public long NextClaimTime { get; init; }

	[JsonPropertyName("reward_item")]
	public RewardItemData? RewardItem { get; init; }
}

internal sealed record RewardItemData {
	[JsonPropertyName("appid")]
	public uint AppId { get; init; }

	[JsonPropertyName("defid")]
	public uint DefId { get; init; }

	[JsonPropertyName("type")]
	public int Type { get; init; }

	/// <summary>Steam returns the cost as a string; "0" identifies a free (giveaway) item.</summary>
	[JsonPropertyName("point_cost")]
	public string? PointCost { get; init; }

	[JsonPropertyName("active")]
	public bool Active { get; init; }

	[JsonPropertyName("community_item_data")]
	public CommunityItemData? CommunityItem { get; init; }
}

internal sealed record CommunityItemData {
	[JsonPropertyName("item_name")]
	public string? ItemName { get; init; }

	[JsonPropertyName("item_title")]
	public string? ItemTitle { get; init; }

	[JsonPropertyName("item_description")]
	public string? ItemDescription { get; init; }
}

/// <summary>Answer of IStoreService/GetDiscoveryQueue.</summary>
internal sealed record GetDiscoveryQueueResponse {
	[JsonPropertyName("appids")]
	public List<uint>? AppIds { get; init; }

	[JsonPropertyName("skipped")]
	public int Skipped { get; init; }

	[JsonPropertyName("exhausted")]
	public bool Exhausted { get; init; }
}

/// <summary>Answer of ILoyaltyRewardsService/QueryRewardItems — point-shop catalog entries.</summary>
internal sealed record QueryRewardItemsResponse {
	[JsonPropertyName("definitions")]
	public List<RewardItemData>? Definitions { get; init; }
}
