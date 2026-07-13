using ArchiSteamFarm.Steam;

namespace Maxisoft.ASF.FreeGames;

internal static class UnpaidStuffDebugLogger {
	private const string Prefix = "[UnpaidStuff]";

	internal static void Log(Bot? bot, string message, string callerName) {
		string prefixedMessage = message.StartsWith(Prefix, System.StringComparison.Ordinal) ? message : $"{Prefix} {message}";

		if (bot is null) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericDebug(prefixedMessage, callerName);

			return;
		}

		bot.ArchiLogger.LogGenericDebug(prefixedMessage, callerName);
	}
}
