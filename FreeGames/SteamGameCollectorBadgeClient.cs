using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Maxisoft.ASF.HttpClientSimple;

namespace Maxisoft.ASF.FreeGames;

#nullable enable

internal sealed class SteamGameCollectorBadgeClient(SimpleHttpClient httpClient) {
	public async Task<ulong?> GetOwnedGamesCount(object? steamId, CancellationToken cancellationToken = default) {
		string? steamIdText = GetSteamIdText(steamId);

		if (string.IsNullOrWhiteSpace(steamIdText)) {
			return null;
		}

		Uri uri = new($"https://steamcommunity.com/profiles/{steamIdText}/badges/13");

		try {
			HttpStreamResponse response = await httpClient.GetStreamAsync(uri, cancellationToken: cancellationToken).ConfigureAwait(false);

			await using (response.ConfigureAwait(false)) {
				if (!response.Response.IsSuccessStatusCode) {
					return null;
				}

				string html = await response.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

				return GameCollectorBadgeParser.ParseOwnedGamesCount(html);
			}
		}
		catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException) {
			return null;
		}
	}

	private static string? GetSteamIdText(object? steamId) =>
		steamId switch {
			null => null,
			ulong value => value.ToString(CultureInfo.InvariantCulture),
			long value when value > 0 => value.ToString(CultureInfo.InvariantCulture),
			IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
			_ => steamId.ToString()
		};
}
