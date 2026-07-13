using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;
using ASFUnpaidStuff.Commands.GetIp;
using ASFUnpaidStuff.Configurations;

namespace ASFUnpaidStuff.Commands {
	// Implement the IBotCommand interface
	internal sealed class CommandDispatcher(ASFUnpaidStuffOptions options) : IBotCommand, IDisposable {
		// Declare a private field for the plugin options instance
		private readonly ASFUnpaidStuffOptions Options = options ?? throw new ArgumentNullException(nameof(options));

		// Declare a private field for the dictionary that maps command names to IBotCommand instances
		private readonly Dictionary<string, IBotCommand> Commands = CreateCommands(options);

		private static Dictionary<string, IBotCommand> CreateCommands(ASFUnpaidStuffOptions options) {
			UnpaidStuffCommand unpaidStuffCommand = new(options);

			return new Dictionary<string, IBotCommand>(StringComparer.OrdinalIgnoreCase) {
				{ "GETIP", new GetIPCommand() },
				{ "UNPAIDSTUFF", unpaidStuffCommand },

				// Legacy keyword kept as an alias so existing user habits, scripts and the pre-rename scheduled commands keep working
				{ "FREEGAMES", unpaidStuffCommand }
			};
		}

		public async Task<string?> Execute(Bot? bot, string message, string[] args, ulong steamID = 0, EAccess access = EAccess.None, CancellationToken cancellationToken = default) {
			try {
				if (args is { Length: > 0 }) {
					// Try to get the corresponding IBotCommand instance from the commands dictionary based on the first argument
					if (Commands.TryGetValue(args[0], out IBotCommand? command)) {
						// Delegate the command execution to the IBotCommand instance, passing the bot and other parameters
						return await command.Execute(bot, message, args, steamID, access, cancellationToken).ConfigureAwait(false);
					}
				}
			}
			catch (Exception ex) {
				// Check if verbose logging is enabled or if the build is in debug mode
				// ReSharper disable once RedundantAssignment
				bool verboseLogging = Options.VerboseLog ?? false;
#if DEBUG
				verboseLogging = true; // Enforce verbose logging in debug mode
#endif

				if (verboseLogging) {
					// Log the detailed stack trace and full description of the exception
					ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericException(ex);
				}
				else {
					// Log a compact error message
					ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericError($"An error occurred: {ex.GetType().Name} {ex.Message}");
				}
			}

			return null; // Return null if an exception occurs or if no command is found
		}

		public void Dispose() {
			HashSet<IBotCommand> seen = [];

			foreach ((_, IBotCommand? value) in Commands) {
				// Aliases map to the same instance — dispose each instance only once
				if (seen.Add(value) && (value is IDisposable disposable)) {
					disposable.Dispose();
				}
			}
		}
	}
}
