using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;
using ASFUnpaidStuff.ASFExtensions.Bot;
using ASFUnpaidStuff.ASFExtensions.Games;
using ASFUnpaidStuff.Configurations;
using Maxisoft.ASF;
using Maxisoft.ASF.ASFExtensions;
using Maxisoft.ASF.ASFExtensions.Games;
using Maxisoft.ASF.AppLists;
using Maxisoft.ASF.Configurations;
using Maxisoft.ASF.FreeGames;
using Maxisoft.ASF.FreeGames.Strategies;
using Maxisoft.ASF.Freebies;
using Maxisoft.ASF.HttpClientSimple;
using Maxisoft.ASF.Reddit;
using Maxisoft.ASF.SteamPackages;
using Maxisoft.ASF.Utils;
using Maxisoft.ASF.Utils.Workarounds;
using SteamKit2;

namespace ASFUnpaidStuff.Commands {
	// Implement the IBotCommand interface
	internal sealed class UnpaidStuffCommand(ASFUnpaidStuffOptions options) : IBotCommand, IDisposable {
		public void Dispose() {
			Strategy.Dispose();

			if (HttpFactory.IsValueCreated) {
				HttpFactory.Value.Dispose();
			}

			SemaphoreSlim?.Dispose();
			Freebies.Dispose();
		}

		internal const string SaveOptionsInternalCommandString = "_SAVEOPTIONS";
		internal const string CollectInternalCommandString = "_COLLECT";
		internal const string FreebiesInternalCommandString = "_FREEBIES";

		private static PluginContext Context => ASFUnpaidStuffPlugin.Context;

		// Declare a private field for the plugin options instance
		private ASFUnpaidStuffOptions Options = options ?? throw new ArgumentNullException(nameof(options));

		private readonly Lazy<SimpleHttpClientFactory> HttpFactory = new(() => new SimpleHttpClientFactory(options));

		public IListFreeGamesStrategy Strategy { get; internal set; } = new ListFreeGamesMainStrategy();
		public EListFreeGamesStrategy PreviousSucessfulStrategy { get; private set; } = EListFreeGamesStrategy.Reddit | EListFreeGamesStrategy.Redlib;

		// Define a constructor that takes an plugin options instance as a parameter

		/// <inheritdoc />
		/// <summary>
		/// Executes the UNPAIDSTUFF command, which allows the user to collect free games from a Reddit list or set or reload the plugin options.
		/// </summary>
		/// <param name="bot">The bot instance that received the command.</param>
		/// <param name="message">The message that contains the command.</param>
		/// <param name="args">The arguments of the command.</param>
		/// <param name="steamID">The SteamID of the user who sent the command.</param>
		/// <param name="cancellationToken"></param>
		/// <returns>A string response that indicates the result of the command execution.</returns>
		public async Task<string?> Execute(Bot? bot, string message, string[] args, ulong steamID = 0, EAccess access = EAccess.None, CancellationToken cancellationToken = default) {
			if (args.Length >= 2) {
				switch (args[1].ToUpperInvariant()) {
					// Options mutation and full config dumps (may reference proxy URLs) are restricted to Master+
					case "SET" when access >= EAccess.Master:
						return await HandleSetCommand(bot, args, cancellationToken).ConfigureAwait(false);
					case "RELOAD" when access >= EAccess.Master:
						return await HandleReloadCommand(bot).ConfigureAwait(false);
					case "CONFIG" or "SETTINGS" when access >= EAccess.Master:
						return await HandleConfigCommand(bot, cancellationToken).ConfigureAwait(false);
					case "BLACKLIST" when access >= EAccess.Master:
						return await HandleBlacklistCommand(bot, args, cancellationToken).ConfigureAwait(false);
					case "INFO" or "STATS" when access >= EAccess.Operator:
						return HandleInfoCommand(bot);
					case "FREEBIES" or "EVENTS" when access >= EAccess.Operator:
						return await HandleFreebiesCommand(bot, args, ignoreCooldowns: true, cancellationToken).ConfigureAwait(false);
					case "HELP":
						return HandleHelpCommand(bot);
					case "SET" or "RELOAD" or "CONFIG" or "SETTINGS" or "BLACKLIST" or "INFO" or "STATS" or "FREEBIES" or "EVENTS":
						// Known subcommand, insufficient access: stay silent like ASF core does
						return null;
					// Internal commands are composed by the plugin itself, which invokes them with EAccess.Owner
					case SaveOptionsInternalCommandString when access >= EAccess.Owner:
						return await HandleInternalSaveOptionsCommand(bot, cancellationToken).ConfigureAwait(false);
					case CollectInternalCommandString when access >= EAccess.Owner:
						return await HandleInternalCollectCommand(bot, args, cancellationToken).ConfigureAwait(false);
					case FreebiesInternalCommandString when access >= EAccess.Owner:
						return await HandleFreebiesCommand(bot, args, ignoreCooldowns: false, cancellationToken).ConfigureAwait(false);
					case SaveOptionsInternalCommandString or CollectInternalCommandString or FreebiesInternalCommandString:
						return null;
				}
			}

			return await HandleCollectCommand(bot).ConfigureAwait(false);
		}

		private static string FormatBotResponse(Bot? bot, string resp) => IBotCommand.FormatBotResponse(bot, resp);

		private async Task<string?> HandleSetCommand(Bot? bot, string[] args, CancellationToken cancellationToken) {
			using CancellationTokenSource cts = CreateLinkedTokenSource(cancellationToken);
			cancellationToken = cts.Token;

			if (args.Length < 3) {
				return FormatBotResponse(bot, "Usage: SET <variable> [value] — see UNPAIDSTUFF HELP for the list of variables");
			}

			string? value = args.Length >= 4 ? args[3].Trim() : null;
			string response;

			switch (args[2].ToUpperInvariant()) {
				case "VERBOSE":
					Options.VerboseLog = true;
					response = "Verbosity on";

					break;
				case "NOVERBOSE":
					Options.VerboseLog = false;
					response = "Verbosity off";

					break;
				case "F2P":
				case "FREETOPLAY":
				case "NOSKIPFREETOPLAY":
					Options.SkipFreeToPlay = false;
					response = $"{ASFUnpaidStuffPlugin.StaticName} is going to collect f2p games";

					break;
				case "NOF2P":
				case "NOFREETOPLAY":
				case "SKIPFREETOPLAY":
					Options.SkipFreeToPlay = true;
					response = $"{ASFUnpaidStuffPlugin.StaticName} is now skipping f2p games";

					break;
				case "DLC":
				case "NOSKIPDLC":
					Options.SkipDLC = false;
					response = $"{ASFUnpaidStuffPlugin.StaticName} is going to collect dlc";

					break;
				case "NODLC":
				case "SKIPDLC":
					Options.SkipDLC = true;
					response = $"{ASFUnpaidStuffPlugin.StaticName} is now skipping dlc";

					break;
				case "FREEBIES":
					Options.Freebies.Enabled = true;
					response = "Automatic freebie scan enabled";

					break;
				case "NOFREEBIES":
					Options.Freebies.Enabled = false;
					response = "Automatic freebie scan disabled (manual FREEBIES command still works)";

					break;
				case "SALEITEMS":
					Options.Freebies.ClaimSaleEventItems = true;
					response = "Sale event item claiming enabled";

					break;
				case "NOSALEITEMS":
					Options.Freebies.ClaimSaleEventItems = false;
					response = "Sale event item claiming disabled";

					break;
				case "POINTSHOP":
					Options.Freebies.ClaimFreePointShopItems = true;
					response = "Free point-shop item claiming enabled";

					break;
				case "NOPOINTSHOP":
					Options.Freebies.ClaimFreePointShopItems = false;
					response = "Free point-shop item claiming disabled";

					break;
				case "QUEUE":
					Options.Freebies.ExploreDiscoveryQueue = true;
					response = "Discovery queue exploration enabled";

					break;
				case "NOQUEUE":
					Options.Freebies.ExploreDiscoveryQueue = false;
					response = "Discovery queue exploration disabled";

					break;
				case "QUEUEPASSES" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int passes):
					Options.Freebies.DiscoveryQueuePasses = Math.Clamp(passes, 1, 5);
					response = $"Discovery queue passes set to {Options.Freebies.DiscoveryQueuePasses}" + ((Options.Freebies.DiscoveryQueuePasses != passes) ? " (clamped to 1..5)" : "");

					break;
				case "QUEUEPASSES":
					return FormatBotResponse(bot, "Usage: SET QUEUEPASSES <1-5>");
				case "SCANINTERVAL" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double hours) && (hours > 0):
					Options.Freebies.ScanInterval = TimeSpan.FromHours(Math.Clamp(hours, 1, 24 * 7));
					response = $"Freebie scan interval set to {Options.Freebies.ScanInterval} (takes effect from the next tick)" + ((hours < 1) ? " — floored to the 1 hour minimum" : "");

					break;
				case "SCANINTERVAL":
					return FormatBotResponse(bot, "Usage: SET SCANINTERVAL <hours> (e.g. 6 or 6.5, minimum 1)");
				case "RECHECKINTERVAL" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) && (minutes > 0):
					// Clamp before TimeSpan.FromMinutes: unbounded doubles (1e11, Infinity) would throw OverflowException
					Options.RecheckInterval = TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60));
					response = $"Free games recheck interval set to {Options.RecheckInterval} (effective delay is jitter-clamped between 11 and 60 minutes)";

					break;
				case "RECHECKINTERVAL":
					return FormatBotResponse(bot, "Usage: SET RECHECKINTERVAL <minutes>");
				case "RANDOMIZE":
					Options.RandomizeRecheckInterval = true;
					response = "Recheck interval randomization enabled";

					break;
				case "NORANDOMIZE":
					Options.RandomizeRecheckInterval = false;
					response = "Recheck interval randomization disabled";

					break;
				case "PROBEBOT" when !string.IsNullOrWhiteSpace(value):
					Options.ProbeBotName = value;
					response = $"Probe bot set to \"{value}\" — new games will be validated on this account before being fanned out";

					break;
				case "PROBEBOT":
					return FormatBotResponse(bot, "Usage: SET PROBEBOT <bot name>");
				case "NOPROBEBOT":
					Options.ProbeBotName = null;
					response = "Probe bot cleared — the first available bot will be used";

					break;
				case "REDDIT":
					Options.Sources.RedditAsfInfo = true;
					response = "Reddit (u/ASFinfo) source enabled";

					break;
				case "NOREDDIT":
					Options.Sources.RedditAsfInfo = false;
					response = "Reddit (u/ASFinfo) source disabled";

					break;
				case "STEAMDB":
					Options.Sources.SteamDbFreeToKeep = true;
					response = "SteamDB free-packages source enabled";

					break;
				case "NOSTEAMDB":
					Options.Sources.SteamDbFreeToKeep = false;
					response = "SteamDB free-packages source disabled";

					break;
				case "PICS":
					Options.Sources.SteamPics = true;
					response = "Steam PICS source enabled";

					break;
				case "NOPICS":
					Options.Sources.SteamPics = false;
					response = "Steam PICS source disabled";

					break;
				case "STORECHECK":
					Options.Sources.SteamStoreProfileCheck = true;
					response = "Steam store profile-features check enabled";

					break;
				case "NOSTORECHECK":
					Options.Sources.SteamStoreProfileCheck = false;
					response = "Steam store profile-features check disabled";

					break;
				default:
					return FormatBotResponse(bot, $"Unknown \"{args[2]}\" variable to set — see UNPAIDSTUFF HELP");
			}

			await SaveOptions(cancellationToken).ConfigureAwait(false);

			return FormatBotResponse(bot, response);
		}

		/// <summary>
		/// Creates a linked cancellation token source from the given cancellation token and the Context cancellation token.
		/// </summary>
		/// <param name="cancellationToken">The cancellation token to link.</param>
		/// <returns>A CancellationTokenSource that is linked to both tokens.</returns>
		private static CancellationTokenSource CreateLinkedTokenSource(CancellationToken cancellationToken) => Context.Valid ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Context.CancellationToken) : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		private Task<string?> HandleReloadCommand(Bot? bot) {
			ASFUnpaidStuffOptionsLoader.Bind(ref Options);

			return Task.FromResult(FormatBotResponse(bot, $"Reloaded {ASFUnpaidStuffPlugin.StaticName} options"))!;
		}

		/// <summary>Human-readable summary of the plugin state and effective settings.</summary>
		private string HandleInfoCommand(Bot? bot) {
			ASFUnpaidStuffFreebieOptions freebies = Options.Freebies;
			Version version = typeof(UnpaidStuffCommand).Assembly.GetName().Version ?? new Version(0, 0);

			StringBuilder sb = new();
			sb.AppendLine(CultureInfo.InvariantCulture, $"{ASFUnpaidStuffPlugin.StaticName} v{version}");
			sb.AppendLine(CultureInfo.InvariantCulture, $"Registered bots: {Context.Bots.Count}");
			sb.AppendLine(CultureInfo.InvariantCulture, $"Probe bot: {Options.ProbeBotName ?? "(first available)"} — validates new games before they are fanned out to the other bots");
			sb.AppendLine(CultureInfo.InvariantCulture, $"Free games recheck interval: {Options.RecheckInterval} (randomized: {Options.RandomizeRecheckInterval ?? true}, effective 11-60 min)");
			sb.AppendLine(CultureInfo.InvariantCulture, $"Sources: reddit={Options.Sources.RedditAsfInfo}, steamDb={Options.Sources.SteamDbFreeToKeep}, steamPics={Options.Sources.SteamPics}, storeCheck={Options.Sources.SteamStoreProfileCheck}");
			sb.AppendLine(CultureInfo.InvariantCulture, $"Skip f2p: {Options.SkipFreeToPlay ?? false}, skip dlc: {Options.SkipDLC ?? false}");
			sb.AppendLine(CultureInfo.InvariantCulture, $"Freebies: enabled={freebies.Enabled}, saleItems={freebies.ClaimSaleEventItems}, pointShop={freebies.ClaimFreePointShopItems}, discoveryQueue={freebies.ExploreDiscoveryQueue} ({freebies.DiscoveryQueuePasses} passes), scanInterval={freebies.ScanInterval}");
			sb.AppendLine(CultureInfo.InvariantCulture, $"Blacklist entries: {Options.Blacklist.Count}");
			sb.Append(CultureInfo.InvariantCulture, $"Config: {ASFUnpaidStuffOptionsLoader.DefaultJsonFile} in ASF config dir, ASF.json key \"ASFUnpaidStuff\", env prefix UNPAIDSTUFF_ — see UNPAIDSTUFF HELP");

			return FormatBotResponse(bot, sb.ToString());
		}

		/// <summary>Dumps the effective configuration as JSON with proxy credentials redacted — chat messages transit Steam servers.</summary>
		private async Task<string?> HandleConfigCommand(Bot? bot, CancellationToken cancellationToken) {
			using CancellationTokenSource cts = CreateLinkedTokenSource(cancellationToken);

			// Work on a serialization round-trip clone so redaction never touches the live options
			ASFUnpaidStuffOptions clone = JsonSerializer.Deserialize<ASFUnpaidStuffOptions>(JsonSerializer.SerializeToUtf8Bytes(Options)) ?? new ASFUnpaidStuffOptions();
			clone.Proxy = RedactProxySecrets(clone.Proxy);
			clone.RedditProxy = RedactProxySecrets(clone.RedditProxy);
			clone.RedlibProxy = RedactProxySecrets(clone.RedlibProxy);

			using MemoryStream memoryStream = new();
			_ = await ASFUnpaidStuffOptionsSaver.SaveOptions(memoryStream, clone, false, cts.Token).ConfigureAwait(false);

			return FormatBotResponse(bot, $"Current {ASFUnpaidStuffPlugin.StaticName} configuration (proxy credentials redacted):\n{Encoding.UTF8.GetString(memoryStream.ToArray())}");
		}

		internal static string? RedactProxySecrets(string? proxyUrl) {
			if (string.IsNullOrEmpty(proxyUrl)) {
				return proxyUrl;
			}

			if (Uri.TryCreate(proxyUrl, UriKind.Absolute, out Uri? uri)) {
				if (string.IsNullOrEmpty(uri.UserInfo)) {
					return proxyUrl;
				}

				UriBuilder builder = new(uri) {
					UserName = "REDACTED",
					Password = ""
				};

				return builder.Uri.ToString();
			}

			return "(redacted)";
		}

		/// <summary>Manages the blacklist: LIST (default), ADD &lt;id|bot/name&gt;, REMOVE &lt;id|bot/name&gt;.</summary>
		private async Task<string?> HandleBlacklistCommand(Bot? bot, string[] args, CancellationToken cancellationToken) {
			using CancellationTokenSource cts = CreateLinkedTokenSource(cancellationToken);
			cancellationToken = cts.Token;

			string action = args.Length >= 3 ? args[2].ToUpperInvariant() : "LIST";
			string? entry = args.Length >= 4 ? args[3].Trim() : null;

			switch (action) {
				case "LIST":
					return FormatBotResponse(bot, Options.Blacklist.Count > 0 ? $"Blacklist: {string.Join(", ", Options.Blacklist)}" : "Blacklist is empty");
				case "ADD" when !string.IsNullOrWhiteSpace(entry): {
					HashSet<string> blacklist = new(Options.Blacklist, StringComparer.InvariantCultureIgnoreCase);

					if (!blacklist.Add(entry)) {
						return FormatBotResponse(bot, $"\"{entry}\" is already blacklisted");
					}

					Options.Blacklist = blacklist;
					await SaveOptions(cancellationToken).ConfigureAwait(false);

					return FormatBotResponse(bot, $"Added \"{entry}\" to the blacklist ({blacklist.Count} entries)");
				}
				case "REMOVE" when !string.IsNullOrWhiteSpace(entry): {
					HashSet<string> blacklist = new(Options.Blacklist, StringComparer.InvariantCultureIgnoreCase);

					if (!blacklist.Remove(entry)) {
						return FormatBotResponse(bot, $"\"{entry}\" is not in the blacklist");
					}

					Options.Blacklist = blacklist;
					await SaveOptions(cancellationToken).ConfigureAwait(false);

					return FormatBotResponse(bot, $"Removed \"{entry}\" from the blacklist ({blacklist.Count} entries)");
				}
				default:
					return FormatBotResponse(bot, "Usage: BLACKLIST [LIST|ADD <appid|s/subid|bot/BotName>|REMOVE <entry>]");
			}
		}

		private static string HandleHelpCommand(Bot? bot) =>
			FormatBotResponse(
				bot,
				"""
				UNPAIDSTUFF commands (FREEGAMES works as an alias):
				  UNPAIDSTUFF — collect free games on all bots now
				  UNPAIDSTUFF FREEBIES|EVENTS [bots] — claim sale event items / point-shop freebies / explore discovery queue now
				  UNPAIDSTUFF INFO — show plugin status and effective settings
				  UNPAIDSTUFF CONFIG — dump the effective configuration as JSON
				  UNPAIDSTUFF BLACKLIST [LIST|ADD <entry>|REMOVE <entry>] — manage the blacklist (game ids or bot/BotName)
				  UNPAIDSTUFF RELOAD — reload options from disk
				  UNPAIDSTUFF SET <variable> [value] — change and persist an option:
				    on/off toggles: VERBOSE, F2P, DLC, FREEBIES, SALEITEMS, POINTSHOP, QUEUE, RANDOMIZE, REDDIT, STEAMDB, PICS, STORECHECK (prefix NO to disable)
				    values: QUEUEPASSES <1-5>, SCANINTERVAL <hours>, RECHECKINTERVAL <minutes>, PROBEBOT <bot name> (NOPROBEBOT to clear)
				Options can also be set in ASF.json under the "ASFUnpaidStuff" key, in config/unpaidstuff.json.config, or via UNPAIDSTUFF_* environment variables.
				"""
			);

		private async Task<string?> HandleCollectCommand(Bot? bot) {
			Bot[] bots = Context.Bots.Count > 0 ? Context.Bots.ToArray() : bot is not null ? [bot] : [];
			int collected = await CollectGames(bots, ECollectGameRequestSource.RequestedByUser, Context.CancellationToken).ConfigureAwait(false);

			return FormatBotResponse(bot, $"Collected a total of {collected} free game(s)");
		}

		private async ValueTask<string?> HandleInternalSaveOptionsCommand(Bot? bot, CancellationToken cancellationToken) {
			await SaveOptions(cancellationToken).ConfigureAwait(false);

			return null;
		}

		private readonly FreebieService Freebies = new();

		/// <summary>
		/// Scans the given (or all) bots for free stuff handed out by Steam sales and events:
		/// daily sale-event items (stickers/cards), discovery-queue exploration and zero-cost point-shop items.
		/// A user-initiated run ignores the per-bot cooldowns; the scheduled internal run honors them.
		/// </summary>
		private async Task<string?> HandleFreebiesCommand(Bot? bot, string[] args, bool ignoreCooldowns, CancellationToken cancellationToken) {
			if (!Options.Freebies.Enabled && !ignoreCooldowns) {
				return null;
			}

			using CancellationTokenSource cts = CreateLinkedTokenSource(cancellationToken);
			cancellationToken = cts.Token;

			List<Bot> bots = [];

			if (args.Length > 2) {
				Dictionary<string, Bot> botMap = Context.Bots.ToDictionary(static b => b.BotName.Trim(), static b => b, StringComparer.InvariantCultureIgnoreCase);

				for (int i = 2; i < args.Length; i++) {
					if (botMap.TryGetValue(args[i].Trim(), out Bot? namedBot)) {
						bots.Add(namedBot);
					}
				}
			}

			if (bots.Count == 0) {
				bots = Context.Bots.Count > 0 ? Context.Bots.ToList() : bot is not null ? [bot] : [];
			}

			if (bots.Count == 0) {
				return FormatBotResponse(bot, "No available bot to scan for freebies");
			}

			FreebieService.FreebieScanSummary summary = await Freebies.Scan(bots, Options, ignoreCooldowns, cancellationToken).ConfigureAwait(false);

			return ignoreCooldowns ? FormatBotResponse(bot, $"Freebies: {summary}") : null;
		}

		private async ValueTask<string?> HandleInternalCollectCommand(Bot? bot, string[] args, CancellationToken cancellationToken) {
			Dictionary<string, Bot> botMap = Context.Bots.ToDictionary(static b => b.BotName.Trim(), static b => b, StringComparer.InvariantCultureIgnoreCase);

			List<Bot> bots = [];

			for (int i = 2; i < args.Length; i++) {
				string botName = args[i].Trim();

				// ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
				if (botMap.TryGetValue(botName, out Bot? savedBot) && savedBot is not null) {
					bots.Add(savedBot);
				}
			}

			if (bots.Count == 0) {
				if (bot is null) {
					return null;
				}

				bots = [bot];
			}

			int collected = await CollectGames(bots, ECollectGameRequestSource.Scheduled, cancellationToken).ConfigureAwait(false);

			return FormatBotResponse(bot, $"Collected a total of {collected} free game(s)" + (bots.Count > 1 ? $" on {bots.Count} bots" : $" on {bots.FirstOrDefault()?.BotName}"));
		}

		private async Task SaveOptions(CancellationToken cancellationToken) {
			using CancellationTokenSource cts = CreateLinkedTokenSource(cancellationToken);
			cancellationToken = cts.Token;
			cts.CancelAfter(10_000);
			await ASFUnpaidStuffOptionsLoader.Save(Options, cancellationToken).ConfigureAwait(false);
		}

		private SemaphoreSlim? SemaphoreSlim;
		private readonly object LockObject = new();
		private readonly HashSet<GameIdentifier> PreviouslySeenAppIds = new();
		private static LoggerFilter LoggerFilter => Context.LoggerFilter;
		private const int BadgePollAttempts = 4;
		private static readonly TimeSpan BadgePollDelay = TimeSpan.FromSeconds(5);
		private bool MissingProbeBotNameWarningLogged;

		// ReSharper disable once RedundantDefaultMemberInitializer
#pragma warning disable CA1805
		internal bool VerboseLog =>
#if DEBUG
			Options.VerboseLog ?? true
#else
		Options.VerboseLog ?? false
#endif
		;
#pragma warning restore CA1805

		private static void DebugLog(Bot? bot, string message) => UnpaidStuffDebugLogger.Log(bot, message, nameof(CollectGames));

		private async Task<int> CollectGames(IEnumerable<Bot> bots, ECollectGameRequestSource requestSource, CancellationToken cancellationToken = default) {
			using CancellationTokenSource cts = CreateLinkedTokenSource(cancellationToken);
			cancellationToken = cts.Token;

			if (cancellationToken.IsCancellationRequested) {
				return 0;
			}

			SemaphoreSlim? semaphore = SemaphoreSlim;

			if (semaphore is null) {
				lock (LockObject) {
					SemaphoreSlim ??= new SemaphoreSlim(1, 1);
					semaphore = SemaphoreSlim;
				}
			}

			if (!await semaphore.WaitAsync(100, cancellationToken).ConfigureAwait(false)) {
				DebugLog(null, "[UnpaidStuff] collection skipped because another UNPAIDSTUFF run is already active");

				return 0;
			}

			Stopwatch stopwatch = Stopwatch.StartNew();
			int res = 0;

			try {
				DebugLog(null, $"[UnpaidStuff] collection started: source={requestSource}");
				IReadOnlyCollection<RedditGameEntry> games = [];

				ListFreeGamesContext strategyContext = new(Options, new Lazy<SimpleHttpClient>(() => HttpFactory.Value.CreateGeneric())) {
					HttpClientFactory = HttpFactory.Value,
					PreviousSucessfulStrategy = PreviousSucessfulStrategy
				};

				if (Options.Sources.RedditAsfInfo) {
					try {
#pragma warning disable CA2000
						games = await Strategy.GetGames(strategyContext, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2000
					}
					catch (Exception e) when (e is InvalidOperationException or JsonException or IOException or RedditServerException or HttpRequestException or AggregateException) {
						if (Options.VerboseLog ?? false) {
							ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericException(e);
						}
						else {
							ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericError($"Unable to get and load json {e.GetType().Name}: {e.Message}");
						}

						return 0;
					}
					finally {
						PreviousSucessfulStrategy = strategyContext.PreviousSucessfulStrategy;

						if (Options.VerboseLog ?? false) {
							ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericInfo($"PreviousSucessfulStrategy = {PreviousSucessfulStrategy}");
						}
					}
				}
				else {
					DebugLog(null, "[UnpaidStuff] Reddit ASFinfo source disabled by config");
				}

#pragma warning disable CA1308
				string remote = strategyContext.PreviousSucessfulStrategy.ToString().ToLowerInvariant();
#pragma warning restore CA1308
				LogNewGameCount(games, remote, VerboseLog || requestSource is ECollectGameRequestSource.RequestedByUser);

				Bot[] availableBots = GetAvailableBots(bots);
				DebugLog(null, $"[UnpaidStuff] {availableBots.Length} available bot(s) after local bot filters");
				Bot? probeBot = ResolveProbeBot(availableBots, requestSource);

				if (probeBot is null) {
					return 0;
				}

				DebugLog(probeBot, $"[UnpaidStuff] using {probeBot.BotName} as probe bot");

				List<CandidateGame> candidates = BuildRedditCandidates(games, out Dictionary<GameIdentifier, int> candidateIndex);
				DebugLog(probeBot, $"[UnpaidStuff] candidate index built from Reddit: {candidateIndex.Count} unique ID(s)");

				using ValidatedGameDatabase validatedGameDatabase = ValidatedGameDatabase.CreateDefault();
				HashSet<GameIdentifier> validatedGameSet = (await validatedGameDatabase.Snapshot(cancellationToken).ConfigureAwait(false)).ToHashSet();
				HashSet<uint> validatedSteamPackageSet = GetValidatedSteamPackageIds(validatedGameSet);
				DebugLog(probeBot, $"[UnpaidStuff] loaded validated DB snapshot before Steam discovery: {validatedGameSet.Count} ID(s), {validatedSteamPackageSet.Count} Steam package(s)");

				using SteamPackageCheckedDatabase steamCheckedDatabase = SteamPackageCheckedDatabase.CreateForProbeBot(probeBot.BotName);
				HashSet<uint> steamCheckedPackageSet = (await steamCheckedDatabase.Snapshot(cancellationToken).ConfigureAwait(false)).ToHashSet();

				// Heal entries poisoned before the AlreadyOwnedByProbe pre-check existed: packages the probe bot owns
				// but that never reached the validated DB were misreported as NotIncremented and marked "checked",
				// which would filter them out below before the ownership pre-check could ever fan them out
				int healedCheckedPackages = steamCheckedPackageSet.RemoveWhere(packageId => {
					if (validatedSteamPackageSet.Contains(packageId)) {
						return false;
					}

					GameIdentifier packageGid = new(packageId, GameIdentifierType.Sub);

					return BotLicenseChecker.BotOwnsIdentifier(probeBot, in packageGid);
				});

				if (healedCheckedPackages > 0) {
					DebugLog(probeBot, $"[UnpaidStuff] re-opened {healedCheckedPackages} probe-owned checked package(s) that were never validated");
				}
				SteamPackageDiscoveryResult steamDiscoveryResult = Options.Sources.SteamPics
					? await SteamPackageDiscoveryService.GetCandidates(probeBot, steamCheckedDatabase, validatedSteamPackageSet, steamCheckedPackageSet, cancellationToken).ConfigureAwait(false)
					: SteamPackageDiscoveryResult.Empty;

				if ((steamDiscoveryResult.NewCandidateCount > 0) || (steamDiscoveryResult.SkippedCheckedCount > 0) || VerboseLog || requestSource is ECollectGameRequestSource.RequestedByUser) {
					probeBot.ArchiLogger.LogGenericInfo($"[UnpaidStuff] Steam packages: {steamDiscoveryResult.NewCandidateCount} new candidate(s), {steamDiscoveryResult.SkippedCheckedCount} already checked, {steamDiscoveryResult.KnownValidatedCount} already validated", nameof(CollectGames));
				}

				CandidateMergeStats steamMergeStats = AddOrPromoteCandidates(candidates, candidateIndex, steamDiscoveryResult.Candidates, EFreeGameCandidateSource.Steam);
				DebugLog(probeBot, $"[UnpaidStuff] merged Steam candidates: {steamMergeStats.Added} added, {steamMergeStats.Promoted} promoted, total={candidates.Count}");

				IReadOnlyCollection<uint> steamDbPackageIds = await SteamDbFreeToKeepService.GetPackageIds(Options, HttpFactory.Value.CreateGeneric(), cancellationToken).ConfigureAwait(false);
				SteamPackagePreFilterResult steamDbPreFilterResult = SteamPackagePreFilter.Apply(steamDbPackageIds, steamCheckedPackageSet, validatedSteamPackageSet);
				CandidateMergeStats steamDbMergeStats = AddOrPromoteCandidates(candidates, candidateIndex, steamDbPreFilterResult.ValidatedCandidates.Concat(steamDbPreFilterResult.RemainingPackages.Select(static packageId => new GameIdentifier(packageId, GameIdentifierType.Sub))), EFreeGameCandidateSource.SteamDb);
				DebugLog(probeBot, $"[UnpaidStuff] merged SteamDB candidates: {steamDbMergeStats.Added} added, {steamDbMergeStats.Promoted} promoted, checkedSkip={steamDbPreFilterResult.CheckedSkippedPackages.Count}, validatedSkip={steamDbPreFilterResult.ValidatedSkippedPackages.Count}, total={candidates.Count}");

				if (candidates.Count == 0) {
					return 0;
				}

				SteamStoreProfileValidationResult storeValidationResult = await SteamStoreProfileFeatureValidator.FilterCandidates(probeBot, candidates.Select(static candidate => candidate.Identifier).ToArray(), Options, HttpFactory.Value.CreateGeneric(), cancellationToken).ConfigureAwait(false);

				if ((storeValidationResult.RejectedLimitedCandidates.Count > 0) || (storeValidationResult.RejectedUnknownCandidates.Count > 0)) {
					HashSet<GameIdentifier> acceptedStoreCandidates = storeValidationResult.AcceptedCandidates.ToHashSet();
					candidates = candidates.Where(candidate => acceptedStoreCandidates.Contains(candidate.Identifier)).ToList();
					probeBot.ArchiLogger.LogGenericInfo($"[UnpaidStuff] Steam Store profile-feature filter: accepted={candidates.Count}, rejectedLimited={storeValidationResult.RejectedLimitedCandidates.Count}, rejectedUnknown={storeValidationResult.RejectedUnknownCandidates.Count}", nameof(CollectGames));
				}

				if (candidates.Count == 0) {
					return 0;
				}

				HashSet<GameIdentifier> failedProbeIds = new();
				bool badgeUnavailableLogged = false;

				DebugLog(probeBot, $"[UnpaidStuff] loaded DB snapshots: {validatedGameSet.Count} validated ID(s), {steamCheckedPackageSet.Count} checked Steam package(s)");

				List<uint> validatedSteamPackagesToMarkChecked = [];

				foreach (CandidateGame candidate in candidates) {
					if (validatedGameSet.Contains(candidate.Identifier) && TryGetSteamPackageId(candidate, out uint packageId) && !steamCheckedPackageSet.Contains(packageId)) {
						validatedSteamPackagesToMarkChecked.Add(packageId);
					}
				}

				if (validatedSteamPackagesToMarkChecked.Count > 0) {
					int added = await steamCheckedDatabase.TryAddMany(validatedSteamPackagesToMarkChecked, cancellationToken).ConfigureAwait(false);
					steamCheckedPackageSet.UnionWith(validatedSteamPackagesToMarkChecked);
					await SteamPackageDiscoveryService.RemovePending(validatedSteamPackagesToMarkChecked, cancellationToken).ConfigureAwait(false);
					DebugLog(probeBot, $"[UnpaidStuff] bulk-marked {validatedSteamPackagesToMarkChecked.Distinct().Count()} already validated Steam package(s) as checked; newly written={added}");
				}

				int skippedFailedProbe = 0;
				int skippedValidated = 0;
				int skippedSteamChecked = 0;
				int probed = 0;
				int validatedNew = 0;
				int notIncremented = 0;
				int badgeUnavailable = 0;

				foreach (CandidateGame candidate in candidates) {
					if (cancellationToken.IsCancellationRequested) {
						break;
					}

					GameIdentifier gid = candidate.Identifier;

					if (failedProbeIds.Contains(gid)) {
						skippedFailedProbe++;
						DebugLog(probeBot, $"[UnpaidStuff] skipping {gid}: failed probe earlier in this run");

						continue;
					}

					if (validatedGameSet.Contains(gid)) {
						skippedValidated++;
						DebugLog(probeBot, $"[UnpaidStuff] {gid} already validated; probing skipped, applying fan-out");

						if (IsSteamPackageCandidate(candidate.Source)) {
							await MarkSteamPackageChecked(candidate, steamCheckedDatabase, steamCheckedPackageSet, cancellationToken).ConfigureAwait(false);
						}

						// The validated DB is global, so the CURRENT probe bot may not own an entry validated by an
						// earlier/other probe bot. For subs the owned-license check inside fan-out skips actual owners,
						// so no exclusion is needed; for apps ownership is undetectable, so keep excluding the probe bot
						// (it owns every app it badge-validated) to avoid re-sending ADDLICENSE to it on every run.
						res += await FanOutValidatedGame(gid, availableBots, gid.Type is GameIdentifierType.App ? probeBot : null, requestSource, cancellationToken).ConfigureAwait(false);

						continue;
					}

					if (TryGetSteamPackageId(candidate, out uint checkedPackageId) && steamCheckedPackageSet.Contains(checkedPackageId)) {
						skippedSteamChecked++;
						await SteamPackageDiscoveryService.RemovePending(checkedPackageId, cancellationToken).ConfigureAwait(false);
						DebugLog(probeBot, $"[UnpaidStuff] skipping already checked Steam package {gid}");

						continue;
					}

					probed++;
					DebugLog(probeBot, $"[UnpaidStuff] probing {gid} from {candidate.Source}: badge before -> ADDLICENSE -> badge after");
					ProbeGameResult probeResult = await ProbeValidatedGame(probeBot, gid, requestSource, cancellationToken).ConfigureAwait(false);
					ProbePersistenceDecision decision = CandidateProbePolicy.Decide(candidate.Source, probeResult.Status);
					DebugLog(probeBot, $"[UnpaidStuff] probe result for {gid}: status={probeResult.Status}, badge={probeResult.Before}->{probeResult.After}, saveValidated={decision.SaveValidated}, saveSteamChecked={decision.SaveSteamChecked}, fanOut={decision.FanOut}, stop={decision.Stop}");

					if (decision.Stop) {
						probeBot.ArchiLogger.LogGenericWarning("[UnpaidStuff] Rate limit reached while probing free games. Skipping remaining games...", nameof(CollectGames));

						return res;
					}

					if (decision.SaveSteamChecked) {
						await MarkSteamPackageChecked(candidate, steamCheckedDatabase, steamCheckedPackageSet, cancellationToken).ConfigureAwait(false);
					}

					int fannedOut = 0;

					if (decision.FanOut) {
						fannedOut = await FanOutValidatedGame(gid, availableBots, probeBot, requestSource, cancellationToken).ConfigureAwait(false);
						res += fannedOut;
					}

					// Bare probe ownership is no proof of claimability: persist an AlreadyOwnedByProbe candidate as
					// validated only once another bot actually managed to claim it
					bool saveValidated = decision.SaveValidated || ((probeResult.Status is EProbeGameStatus.AlreadyOwnedByProbe) && (fannedOut > 0));

					if (saveValidated) {
						await validatedGameDatabase.TryAdd(gid, cancellationToken).ConfigureAwait(false);
						validatedGameSet.Add(gid);
						validatedNew++;

						if (probeResult.Status is EProbeGameStatus.AlreadyOwnedByProbe) {
							probeBot.ArchiLogger.LogGenericInfo($"[UnpaidStuff] {gid} already owned by probe bot and claimed by {fannedOut} other bot(s); recorded as validated", nameof(CollectGames));
						}
						else {
							probeBot.ArchiLogger.LogGenericInfo($"[UnpaidStuff] validated {gid} with Game Collector badge ({probeResult.Before} -> {probeResult.After})", nameof(CollectGames));
							res++;
						}
					}
					else if (probeResult.Status is EProbeGameStatus.AlreadyOwnedByProbe) {
						DebugLog(probeBot, $"[UnpaidStuff] {gid} owned by probe bot but not claimed by any other bot; not persisting as validated");
					}
					else if (probeResult.Status is EProbeGameStatus.BadgeUnavailable) {
						failedProbeIds.Add(gid);
						badgeUnavailable++;

						if (!badgeUnavailableLogged) {
							probeBot.ArchiLogger.LogGenericWarning($"[UnpaidStuff] Cannot read Game Collector badge for probe bot {probeBot.BotName}; new IDs will not be saved or fanned out until the badge page is readable", nameof(CollectGames));
							badgeUnavailableLogged = true;
						}
					}
					else if (probeResult.Status is EProbeGameStatus.NotIncremented) {
						failedProbeIds.Add(gid);
						notIncremented++;

						if (VerboseLog || requestSource is ECollectGameRequestSource.RequestedByUser) {
							probeBot.ArchiLogger.LogGenericInfo($"[UnpaidStuff] ignored {gid}: Game Collector badge did not increase ({probeResult.Before} -> {probeResult.After})", nameof(CollectGames));
						}
					}
				}

				DebugLog(probeBot, $"[UnpaidStuff] collection summary: candidates={candidates.Count}, probed={probed}, alreadyValidated={skippedValidated}, steamCheckedSkip={skippedSteamChecked}, failedProbeSkip={skippedFailedProbe}, newValidated={validatedNew}, notIncremented={notIncremented}, badgeUnavailable={badgeUnavailable}, collectedOps={res}, elapsedMs={stopwatch.ElapsedMilliseconds}");

				foreach (Bot bot in availableBots) {
					Context.BotContexts.GetBotContext(bot)?.NewRun();
				}
			}
			catch (OperationCanceledException) {
				DebugLog(null, $"[UnpaidStuff] collection cancelled after {stopwatch.ElapsedMilliseconds} ms");
			}
			finally {
				DebugLog(null, $"[UnpaidStuff] collection finished: collectedOps={res}, elapsedMs={stopwatch.ElapsedMilliseconds}");
				semaphore.Release();
			}

			return res;
		}

		private List<CandidateGame> BuildRedditCandidates(IEnumerable<RedditGameEntry> games, out Dictionary<GameIdentifier, int> candidateIndex) {
			candidateIndex = new();
			List<CandidateGame> candidates = [];
			int recentEntryCount = 0;
			int oldEntryCount = 0;
			int skippedFreeToPlay = 0;
			int skippedDlc = 0;
			int emptyIdentifier = 0;
			int malformedIdentifier = 0;
			int blacklisted = 0;
			int duplicate = 0;

			foreach (RedditGameEntry entry in games) {
					if (!RedditCandidateAgeFilter.IsRecent(entry, DateTimeOffset.UtcNow, Options.Reddit)) {
						oldEntryCount++;

						continue;
				}

				recentEntryCount++;

				if (entry.IsFreeToPlay && Options.SkipFreeToPlay is true) {
					skippedFreeToPlay++;

					continue;
				}

				if (entry.IsForDlc && Options.SkipDLC is true) {
					skippedDlc++;

					continue;
				}

				if (string.IsNullOrWhiteSpace(entry.Identifier)) {
					emptyIdentifier++;

					continue;
				}

				foreach (string subIdentifier in entry.Identifier.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
					if (!GameIdentifier.TryParse(subIdentifier, out GameIdentifier gid)) {
						malformedIdentifier++;

						continue;
					}

					if (Options.IsBlacklisted(in gid)) {
						blacklisted++;

						continue;
					}

					if (candidateIndex.TryAdd(gid, candidates.Count)) {
						candidates.Add(new CandidateGame(gid, EFreeGameCandidateSource.Reddit));
					}
					else {
						duplicate++;
					}
				}
			}

			string ageFilterDescription = Options.Reddit.MaxAgeDays is null ? "all-time" : $"{Options.Reddit.MaxAgeDays.Value}-day";
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericInfo($"[UnpaidStuff] Reddit candidates after {ageFilterDescription} filter: {candidates.Count} ID(s) from {recentEntryCount} accepted entry/entries; skipped {oldEntryCount} old/invalid entry/entries", nameof(CollectGames));

			if (VerboseLog) {
				DebugLog(null, $"[UnpaidStuff] {candidates.Count} unique Reddit candidate ID(s) after local filtering");
			}

			DebugLog(null, $"[UnpaidStuff] Reddit candidate filter details: recentEntries={recentEntryCount}, oldEntries={oldEntryCount}, skippedF2P={skippedFreeToPlay}, skippedDLC={skippedDlc}, emptyIdentifier={emptyIdentifier}, malformedIdentifier={malformedIdentifier}, blacklisted={blacklisted}, duplicates={duplicate}, unique={candidates.Count}");

			return candidates;
		}

		private static CandidateMergeStats AddOrPromoteCandidates(List<CandidateGame> candidates, Dictionary<GameIdentifier, int> index, IEnumerable<GameIdentifier> identifiers, EFreeGameCandidateSource source) {
			int added = 0;
			int promoted = 0;

			foreach (GameIdentifier identifier in identifiers) {
				if (index.TryGetValue(identifier, out int existingIndex)) {
					candidates[existingIndex] = candidates[existingIndex] with { Source = candidates[existingIndex].Source | source };
					promoted++;

					continue;
				}

				index[identifier] = candidates.Count;
				candidates.Add(new CandidateGame(identifier, source));
				added++;
			}

			return new CandidateMergeStats(added, promoted);
		}

		private static async Task<bool> MarkSteamPackageChecked(CandidateGame candidate, SteamPackageCheckedDatabase checkedDatabase, ISet<uint>? checkedPackageSet, CancellationToken cancellationToken) {
			if (!TryGetSteamPackageId(candidate, out uint packageId)) {
				return false;
			}

			if (checkedPackageSet?.Contains(packageId) is true) {
				await SteamPackageDiscoveryService.RemovePending(packageId, cancellationToken).ConfigureAwait(false);

				return false;
			}

			bool added = await checkedDatabase.TryAdd(packageId, cancellationToken).ConfigureAwait(false);
			checkedPackageSet?.Add(packageId);
			await SteamPackageDiscoveryService.RemovePending(packageId, cancellationToken).ConfigureAwait(false);

			DebugLog(null, $"[UnpaidStuff] Steam package {candidate.Identifier} marked as checked for probe scope; added={added}");

			return added;
		}

		private static bool TryGetSteamPackageId(CandidateGame candidate, out uint packageId) {
			packageId = 0;

			if (!IsSteamPackageCandidate(candidate.Source) || candidate.Identifier.Type is not GameIdentifierType.Sub || (candidate.Identifier.Id <= 0) || (candidate.Identifier.Id > uint.MaxValue)) {
				return false;
			}

			packageId = checked((uint) candidate.Identifier.Id);

			return true;
		}

		private static bool IsSteamPackageCandidate(EFreeGameCandidateSource source) => source.HasFlag(EFreeGameCandidateSource.Steam) || source.HasFlag(EFreeGameCandidateSource.SteamDb);

		private static HashSet<uint> GetValidatedSteamPackageIds(IEnumerable<GameIdentifier> validatedGameIdentifiers) {
			HashSet<uint> res = [];

			foreach (GameIdentifier identifier in validatedGameIdentifiers) {
				if (identifier.Type is GameIdentifierType.Sub && (identifier.Id > 0) && (identifier.Id <= uint.MaxValue)) {
					res.Add(checked((uint) identifier.Id));
				}
			}

			return res;
		}

		private async Task<int> FanOutValidatedGame(GameIdentifier gid, IReadOnlyCollection<Bot> bots, Bot? excludedBot, ECollectGameRequestSource requestSource, CancellationToken cancellationToken) {
			int res = 0;
			int attempted = 0;
			int skippedProbe = 0;
			int skippedOwned = 0;
			int rateLimited = 0;

			DebugLog(excludedBot, $"[UnpaidStuff] fan-out started for {gid} across {bots.Count} bot(s)");

			foreach (Bot bot in bots) {
				if (cancellationToken.IsCancellationRequested) {
					break;
				}

				if (ReferenceEquals(bot, excludedBot)) {
					skippedProbe++;

					continue;
				}

				if (BotLicenseChecker.BotOwnsIdentifier(bot, in gid)) {
					skippedOwned++;

					if (VerboseLog) {
						bot.ArchiLogger.LogGenericDebug($"[UnpaidStuff] skipping {gid}: license is already visible on {bot.BotName}", nameof(CollectGames));
					}

					continue;
				}

				attempted++;
				AddLicenseResult addLicenseResult = await ExecuteAddLicense(bot, gid, requestSource).ConfigureAwait(false);

				if (addLicenseResult.Success) {
					res++;
				}

				if (addLicenseResult.RateLimited) {
					rateLimited++;
					bot.ArchiLogger.LogGenericWarning("[UnpaidStuff] Rate limit reached while applying validated free games. Skipping remaining bots for this ID...", nameof(CollectGames));

					break;
				}
			}

			DebugLog(excludedBot, $"[UnpaidStuff] fan-out finished for {gid}: attempted={attempted}, success={res}, skippedProbe={skippedProbe}, skippedOwned={skippedOwned}, rateLimited={rateLimited}");

			return res;
		}

		private Bot[] GetAvailableBots(IEnumerable<Bot> bots) {
			List<Bot> res = [];
			HashSet<Bot> seen = new(new BotEqualityComparer());
			int total = 0;
			int nullBot = 0;
			int duplicate = 0;
			int disconnected = 0;
			int backgroundRedeem = 0;
			int blacklisted = 0;

			foreach (Bot bot in bots) {
				total++;

				if (bot is null) {
					nullBot++;

					continue;
				}

				if (!seen.Add(bot)) {
					duplicate++;

					continue;
				}

				if (!bot.IsConnectedAndLoggedOn) {
					disconnected++;

					continue;
				}

				if (bot.GamesToRedeemInBackgroundCount > 0) {
					backgroundRedeem++;

					continue;
				}

				if (Options.IsBlacklisted(bot)) {
					blacklisted++;

					continue;
				}

				res.Add(bot);
			}

			DebugLog(null, $"[UnpaidStuff] bot filter details: total={total}, available={res.Count}, null={nullBot}, duplicate={duplicate}, disconnected={disconnected}, backgroundRedeem={backgroundRedeem}, blacklisted={blacklisted}");

			return res.ToArray();
		}

		private Bot? ResolveProbeBot(IReadOnlyCollection<Bot> availableBots, ECollectGameRequestSource requestSource) {
			if (availableBots.Count == 0) {
				DebugLog(null, "[UnpaidStuff] no available bot found for free games probe");

				return null;
			}

			string? probeBotName = Options.ProbeBotName;

			if (!string.IsNullOrWhiteSpace(probeBotName)) {
				Bot? configuredProbeBot = availableBots.FirstOrDefault(bot => bot.BotName.Equals(probeBotName, StringComparison.OrdinalIgnoreCase));

				if (configuredProbeBot is not null) {
					return configuredProbeBot;
				}

				ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarning($"[UnpaidStuff] configured probe bot \"{probeBotName}\" is not available or logged in; skipping collection", nameof(CollectGames));

				return null;
			}

			Bot probeBot = availableBots.First();

			if (!MissingProbeBotNameWarningLogged || requestSource is ECollectGameRequestSource.RequestedByUser) {
				ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarning($"[UnpaidStuff] ProbeBotName is not configured; using {probeBot.BotName} as the temporary probe bot", nameof(CollectGames));
				MissingProbeBotNameWarningLogged = true;
			}

			return probeBot;
		}

		private async Task<AddLicenseResult> ExecuteAddLicense(Bot bot, GameIdentifier gid, ECollectGameRequestSource requestSource) {
			string cmd = $"ADDLICENSE {bot.BotName} {gid}";

			if (VerboseLog) {
				DebugLog(bot, $"Trying to perform command \"{cmd}\"");
			}

			BotContext? context = Context.BotContexts.GetBotContext(bot);
			bool hideErrorLog = !VerboseLog && (requestSource is not ECollectGameRequestSource.RequestedByUser) && (context?.ShouldHideErrorLogForApp(in gid) ?? false);
			string? resp;

			using (LoggerFilter.DisableLoggingForAddLicenseCommonErrors(_ => hideErrorLog, bot)) {
				resp = await bot.Commands.Response(EAccess.Operator, cmd).ConfigureAwait(false);
			}

			bool success = false;
			bool rateLimited = false;

			if (!string.IsNullOrWhiteSpace(resp)) {
				success = resp.Contains("collected game", StringComparison.InvariantCultureIgnoreCase);
				success |= resp.Contains("OK", StringComparison.InvariantCultureIgnoreCase);
				rateLimited = resp.Contains("RateLimited", StringComparison.InvariantCultureIgnoreCase);
				DebugLog(bot, $"[UnpaidStuff] ADDLICENSE result for {gid}: success={success}, rateLimited={rateLimited}, responseLength={resp.Length}");
				BotPackageChecker.RemoveBotCache(bot);

				if (success || VerboseLog || requestSource is ECollectGameRequestSource.RequestedByUser || !hideErrorLog) {
					bot.ArchiLogger.LogGenericInfo($"[UnpaidStuff] {resp}", nameof(CollectGames));
				}
			}
			else {
				DebugLog(bot, $"[UnpaidStuff] ADDLICENSE returned an empty response for {gid}");
				BotPackageChecker.RemoveBotCache(bot);
			}

			return new AddLicenseResult(success, rateLimited);
		}

		private async Task<ProbeGameResult> ProbeValidatedGame(Bot probeBot, GameIdentifier gid, ECollectGameRequestSource requestSource, CancellationToken cancellationToken) {
			if (BotLicenseChecker.BotOwnsIdentifier(probeBot, in gid)) {
				// The badge cannot increment for a game the probe account already owns: probing it would
				// misreport a perfectly good game as NotIncremented and block the fan-out forever
				DebugLog(probeBot, $"[UnpaidStuff] {gid} is already owned by probe bot {probeBot.BotName}; skipping probe, fanning out directly");

				return new ProbeGameResult(EProbeGameStatus.AlreadyOwnedByProbe, null, null);
			}

			ulong? before = null;

			if (Options.Probe.RequireBadgeIncrement) {
				DebugLog(probeBot, $"[UnpaidStuff] reading Game Collector badge before probing {gid}");
				before = await ReadBadgeCount(probeBot, cancellationToken).ConfigureAwait(false);

				if (before is null) {
					DebugLog(probeBot, $"[UnpaidStuff] badge before probing {gid} is unavailable");

					return new ProbeGameResult(EProbeGameStatus.BadgeUnavailable, null, null);
				}

				DebugLog(probeBot, $"[UnpaidStuff] badge before probing {gid}: {before}");
			}

			AddLicenseResult addLicenseResult = await ExecuteAddLicense(probeBot, gid, requestSource).ConfigureAwait(false);

			if (addLicenseResult.RateLimited) {
				DebugLog(probeBot, $"[UnpaidStuff] probing {gid} stopped by ADDLICENSE rate limit");

				return new ProbeGameResult(EProbeGameStatus.RateLimited, before, before);
			}

			bool ownsLicense = true;

			if (Options.Probe.RequireAccountLicense) {
				ownsLicense = await PollLicenseOwnershipAfter(probeBot, gid, cancellationToken).ConfigureAwait(false);
				DebugLog(probeBot, $"[UnpaidStuff] license ownership after probing {gid}: {ownsLicense}");
			}

			ulong? after = before;

			if (Options.Probe.RequireBadgeIncrement) {
				after = await PollBadgeCountAfter(probeBot, before!.Value, cancellationToken).ConfigureAwait(false);

				if (after is null) {
					DebugLog(probeBot, $"[UnpaidStuff] badge after probing {gid} is unavailable");

					return new ProbeGameResult(EProbeGameStatus.BadgeUnavailable, before, null);
				}

				DebugLog(probeBot, $"[UnpaidStuff] badge after probing {gid}: {after}");
			}

			EProbeGameStatus status = ProbeGameOutcomeEvaluator.Evaluate(Options.Probe.RequireBadgeIncrement, before, after, ownsLicense);

			return new ProbeGameResult(status, before, after);
		}

		private static async Task<bool> PollLicenseOwnershipAfter(Bot probeBot, GameIdentifier gid, CancellationToken cancellationToken) {
			for (int attempt = 0; attempt < BadgePollAttempts; attempt++) {
				if (attempt > 0) {
					await Task.Delay(BadgePollDelay, cancellationToken).ConfigureAwait(false);
				}

				BotPackageChecker.RemoveBotCache(probeBot);

				if (BotLicenseChecker.BotOwnsIdentifier(probeBot, in gid)) {
					return true;
				}
			}

			return false;
		}

		private async Task<ulong?> PollBadgeCountAfter(Bot probeBot, ulong before, CancellationToken cancellationToken) {
			ulong? last = null;

			for (int attempt = 0; attempt < BadgePollAttempts; attempt++) {
				if (attempt > 0) {
					await Task.Delay(BadgePollDelay, cancellationToken).ConfigureAwait(false);
				}

				ulong? current = await ReadBadgeCount(probeBot, cancellationToken).ConfigureAwait(false);

				if (current is null) {
					DebugLog(probeBot, $"[UnpaidStuff] badge poll attempt {attempt + 1}/{BadgePollAttempts}: unavailable");

					continue;
				}

				last = current;
				DebugLog(probeBot, $"[UnpaidStuff] badge poll attempt {attempt + 1}/{BadgePollAttempts}: {current} (before={before})");

				if (current.Value > before) {
					return current;
				}
			}

			return last;
		}

		private Task<ulong?> ReadBadgeCount(Bot bot, CancellationToken cancellationToken) {
			SteamGameCollectorBadgeClient badgeClient = new(HttpFactory.Value.CreateGeneric());

			return badgeClient.GetOwnedGamesCount(bot.SteamID, cancellationToken);
		}

		private readonly record struct AddLicenseResult(bool Success, bool RateLimited);

		private readonly record struct ProbeGameResult(EProbeGameStatus Status, ulong? Before, ulong? After);

		private readonly record struct CandidateGame(GameIdentifier Identifier, EFreeGameCandidateSource Source);

		private readonly record struct CandidateMergeStats(int Added, int Promoted);

		private void LogNewGameCount(IReadOnlyCollection<RedditGameEntry> games, string remote, bool logZero = false) {
			int totalAppIdCounter = PreviouslySeenAppIds.Count;
			int newGameCounter = 0;

			foreach (RedditGameEntry entry in games) {
				if (GameIdentifier.TryParse(entry.Identifier, out GameIdentifier identifier) && PreviouslySeenAppIds.Add(identifier)) {
					newGameCounter++;
				}
			}

			if ((totalAppIdCounter == 0) && (games.Count > 0)) {
				ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericInfo($"[UnpaidStuff] found potentially {games.Count} free games on {remote}", nameof(CollectGames));
			}
			else if (newGameCounter > 0) {
				ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericInfo($"[UnpaidStuff] found {newGameCounter} fresh free game(s) on {remote}", nameof(CollectGames));
			}
			else if ((newGameCounter == 0) && logZero) {
				ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericInfo($"[UnpaidStuff] found 0 new game out of {games.Count} free games on {remote}", nameof(CollectGames));
			}
		}

	}
}
