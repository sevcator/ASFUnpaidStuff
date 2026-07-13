using ArchiSteamFarm.Steam;
using ASFUnpaidStuff.ASFExtensions.Games;
using Maxisoft.ASF.ASFExtensions.Games;

namespace Maxisoft.ASF.Utils.Workarounds;

#nullable enable

public static class BotLicenseChecker {
	public static bool BotOwnsIdentifier(Bot? bot, in GameIdentifier gameIdentifier) {
		if ((bot is null) || !gameIdentifier.Valid || (gameIdentifier.Id > uint.MaxValue)) {
			return false;
		}

		uint id = checked((uint) gameIdentifier.Id);

		return gameIdentifier.Type switch {
			GameIdentifierType.Sub => BotPackageChecker.BotOwnsPackage(bot, id),

			// ASF exposes only OwnedPackages (keyed by package id) and no queryable owned-apps list; an app id is not a
			// package id, so app ownership cannot be determined reliably here. Report "not owned" so the fan-out still
			// attempts the app on every bot (ASF treats an already-owned app as a harmless no-op), instead of risking a
			// false positive that would wrongly skip a bot.
			_ => false
		};
	}
}
