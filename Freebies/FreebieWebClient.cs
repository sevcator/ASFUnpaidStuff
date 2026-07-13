using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Integration;
using ArchiSteamFarm.Web.Responses;

namespace Maxisoft.ASF.Freebies;

/// <summary>
/// Thin wrappers around the Steam web-api endpoints handing out free stuff.
/// Every call goes through the bot's authenticated <see cref="ArchiWebHandler"/> session and
/// authenticates via the bot's <see cref="Bot.AccessToken"/> passed as a request parameter
/// (the same pattern the official Steam client and other ASF plugins use — no sessionid form field needed).
/// </summary>
internal static class FreebieWebClient {
	private static readonly Uri SteamApiURL = new("https://api.steampowered.com");

	/// <summary>
	/// Claims the currently pending free sale-event item (sticker/card/etc.) if any.
	/// Returns null on network/auth failure; a response with a null RewardItem means nothing was claimable.
	/// </summary>
	public static async Task<ClaimItemResponse?> ClaimSaleItem(Bot bot, CancellationToken cancellationToken) {
		if (!TryGetAccessToken(bot, out string? token)) {
			return null;
		}

		Uri request = new(SteamApiURL, $"/ISaleItemRewardsService/ClaimItem/v1?access_token={Uri.EscapeDataString(token)}");

		ObjectResponse<SteamApiResponse<ClaimItemResponse>>? response = await bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<SteamApiResponse<ClaimItemResponse>>(
			request,
			data: (IDictionary<string, string>?) null,
			referer: ArchiWebHandler.SteamStoreURL,
			session: ArchiWebHandler.ESession.None,
			cancellationToken: cancellationToken
		).ConfigureAwait(false);

		return response?.Content?.Response;
	}

	/// <summary>Fetches (and forces a rebuild of) the bot's store discovery queue.</summary>
	public static async Task<GetDiscoveryQueueResponse?> GetDiscoveryQueue(Bot bot, CancellationToken cancellationToken) {
		if (!TryGetAccessToken(bot, out string? token)) {
			return null;
		}

		Uri request = new(SteamApiURL, $"/IStoreService/GetDiscoveryQueue/v1/?access_token={Uri.EscapeDataString(token)}&rebuild_queue=1&queue_type=0&ignore_user_preferences=1");

		ObjectResponse<SteamApiResponse<GetDiscoveryQueueResponse>>? response = await bot.ArchiWebHandler.UrlGetToJsonObjectWithSession<SteamApiResponse<GetDiscoveryQueueResponse>>(
			request,
			referer: ArchiWebHandler.SteamStoreURL,
			cancellationToken: cancellationToken
		).ConfigureAwait(false);

		return response?.Content?.Response;
	}

	/// <summary>Marks one app of the discovery queue as seen ("skips" it), advancing the queue.</summary>
	public static async Task<bool> SkipDiscoveryQueueItem(Bot bot, uint appId, CancellationToken cancellationToken) {
		if (!TryGetAccessToken(bot, out string? token)) {
			return false;
		}

		Uri request = new(SteamApiURL, $"/IStoreService/SkipDiscoveryQueueItem/v1/?access_token={Uri.EscapeDataString(token)}&appid={appId.ToString(CultureInfo.InvariantCulture)}");

		return await bot.ArchiWebHandler.UrlPostWithSession(
			request,
			data: (IDictionary<string, string>?) null,
			referer: ArchiWebHandler.SteamStoreURL,
			session: ArchiWebHandler.ESession.None,
			cancellationToken: cancellationToken
		).ConfigureAwait(false);
	}

	/// <summary>Lists the point-shop reward catalog currently offered to this account (free giveaway entries have point_cost "0").</summary>
	public static async Task<QueryRewardItemsResponse?> QueryRewardItems(Bot bot, CancellationToken cancellationToken) {
		if (!TryGetAccessToken(bot, out string? token)) {
			return null;
		}

		Uri request = new(SteamApiURL, $"/ILoyaltyRewardsService/QueryRewardItems/v1/?access_token={Uri.EscapeDataString(token)}");

		ObjectResponse<SteamApiResponse<QueryRewardItemsResponse>>? response = await bot.ArchiWebHandler.UrlGetToJsonObjectWithSession<SteamApiResponse<QueryRewardItemsResponse>>(
			request,
			referer: ArchiWebHandler.SteamStoreURL,
			cancellationToken: cancellationToken
		).ConfigureAwait(false);

		return response?.Content?.Response;
	}

	/// <summary>
	/// Redeems one point-shop item by definition id; used only for entries costing 0 points.
	/// Returns null when no request could be attempted (bot logged out / no token), so callers can retry later.
	/// </summary>
	public static async Task<bool?> RedeemPoints(Bot bot, uint defId, CancellationToken cancellationToken) {
		if (!TryGetAccessToken(bot, out string? token)) {
			return null;
		}

		Uri request = new(SteamApiURL, "/ILoyaltyRewardsService/RedeemPoints/v1/");

		Dictionary<string, string> data = new(2, StringComparer.Ordinal) {
			{ "access_token", token },
			{ "defid", defId.ToString(CultureInfo.InvariantCulture) }
		};

		return await bot.ArchiWebHandler.UrlPostWithSession(
			request,
			data: data,
			referer: ArchiWebHandler.SteamStoreURL,
			session: ArchiWebHandler.ESession.None,
			cancellationToken: cancellationToken
		).ConfigureAwait(false);
	}

	private static bool TryGetAccessToken(Bot bot, out string token) {
		string? accessToken = bot.IsConnectedAndLoggedOn ? bot.AccessToken : null;

		if (string.IsNullOrEmpty(accessToken)) {
			token = string.Empty;

			return false;
		}

		token = accessToken;

		return true;
	}
}
