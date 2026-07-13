using System;
using System.IO;
using ArchiSteamFarm;
using Maxisoft.ASF.SteamPackages;

namespace Maxisoft.ASF.Configurations;

/// <summary>
/// One-time migration of on-disk files created under the plugin's previous identity (ASFFreeGames),
/// so existing user configuration and collected-game state survive the rename to ASFUnpaidStuff.
/// </summary>
internal static class LegacyFilesMigrator {
	private const string LegacyJsonFile = "freegames.json.config";
	private const string LegacyValidatedFileName = "freegames.validated.txt";
	private const string LegacyStateFileName = "freegames.steam.state.cache";
	private const string LegacyCheckedFilePrefix = "freegames.steam.checked.";
	private const string CheckedFilePrefix = "unpaidstuff.steam.checked.";

	public static void MigrateIfNeeded() {
		string basePath;

		try {
			basePath = Path.GetFullPath(SharedInfo.ConfigDirectory);
		}
		catch (Exception ex) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericDebug($"{nameof(LegacyFilesMigrator)}: unable to resolve config directory: {ex.Message}");

			return;
		}

		if (!Directory.Exists(basePath)) {
			return;
		}

		TryMoveFile(Path.Combine(basePath, LegacyJsonFile), Path.Combine(basePath, ASFUnpaidStuffOptionsLoader.DefaultJsonFile));
		TryMoveFile(Path.Combine(basePath, LegacyValidatedFileName), Path.Combine(basePath, AppLists.ValidatedGameDatabase.DefaultFileName));
		TryMoveFile(Path.Combine(basePath, LegacyStateFileName), Path.Combine(basePath, SteamPackageState.DefaultFileName));

		try {
			foreach (string legacyPath in Directory.EnumerateFiles(basePath, $"{LegacyCheckedFilePrefix}*.txt")) {
				string fileName = Path.GetFileName(legacyPath);

				if (!fileName.StartsWith(LegacyCheckedFilePrefix, StringComparison.Ordinal)) {
					continue;
				}

				TryMoveFile(legacyPath, Path.Combine(basePath, CheckedFilePrefix + fileName[LegacyCheckedFilePrefix.Length..]));
			}
		}
		catch (Exception ex) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericDebug($"{nameof(LegacyFilesMigrator)}: unable to enumerate legacy checked-package files: {ex.Message}");
		}
	}

	private static void TryMoveFile(string source, string destination) {
		try {
			if (!File.Exists(source) || File.Exists(destination)) {
				return;
			}

			File.Move(source, destination);
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericInfo($"{nameof(LegacyFilesMigrator)}: migrated legacy file {Path.GetFileName(source)} -> {Path.GetFileName(destination)}");
		}
		catch (Exception ex) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarning($"{nameof(LegacyFilesMigrator)}: failed to migrate {Path.GetFileName(source)}: {ex.Message}");
		}
	}
}
