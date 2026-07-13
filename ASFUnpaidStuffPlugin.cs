using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Collections;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ASFUnpaidStuff.ASFExtensions.Bot;
using ASFUnpaidStuff.Commands;
using ASFUnpaidStuff.Configurations;
using JetBrains.Annotations;
using Maxisoft.ASF.ASFExtensions;
using Maxisoft.ASF.Configurations;
using Maxisoft.ASF.Github;
using Maxisoft.ASF.SteamPackages;
using Maxisoft.ASF.Utils;
using Maxisoft.ASF.Utils.Workarounds;
using SteamKit2;
using static ArchiSteamFarm.Core.ASF;

namespace Maxisoft.ASF;

internal interface IASFUnpaidStuffPlugin {
	internal Version Version { get; }
	internal ASFUnpaidStuffOptions Options { get; }

	internal void CollectGamesOnClock(object? source);

	internal void ScanFreebiesOnClock(object? source);
}

#pragma warning disable CA1812 // ASF uses this class during runtime
[SuppressMessage("Design", "CA1001:Disposable fields")]
internal sealed class ASFUnpaidStuffPlugin : IASF, IBot, IBotConnection, IBotCommand2, ISteamPICSChanges, IUpdateAware, IASFUnpaidStuffPlugin, IGitHubPluginUpdates {
	internal const string StaticName = nameof(ASFUnpaidStuffPlugin);
	private const int CollectGamesTimeout = 3 * 60 * 1000;

	private static readonly PluginContext EmptyContext = new(Array.Empty<Bot>(), new ContextRegistry(), new ASFUnpaidStuffOptions(), new LoggerFilter());

	internal static PluginContext Context {
		get => _context.Value ?? EmptyContext;
		private set => _context.Value = value;
	}

	// ReSharper disable once InconsistentNaming
	private static readonly Utils.Workarounds.AsyncLocal<PluginContext> _context = new();
	private static CancellationToken CancellationToken => Context.CancellationToken;

	public string Name => StaticName;
	public Version Version => GetVersion();

	private static Version GetVersion() => typeof(ASFUnpaidStuffPlugin).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	private readonly ConcurrentHashSet<Bot> Bots = new(new BotEqualityComparer());
	private readonly Lazy<CancellationTokenSource> CancellationTokenSourceLazy = new(static () => new CancellationTokenSource());
	private readonly CommandDispatcher CommandDispatcher;

	private readonly LoggerFilter LoggerFilter = new();

	private bool VerboseLog => Options.VerboseLog ?? true;
	private readonly ContextRegistry BotContextRegistry = new();

	public ASFUnpaidStuffOptions Options => OptionsField;
	private ASFUnpaidStuffOptions OptionsField = new();

	private readonly CollectIntervalManager CollectIntervalManager;
	private readonly FreebieScanIntervalManager FreebieScanIntervalManager;

	public ASFUnpaidStuffPlugin() {
		CommandDispatcher = new CommandDispatcher(Options);
		CollectIntervalManager = new CollectIntervalManager(this);
		FreebieScanIntervalManager = new FreebieScanIntervalManager(this);
		_context.Value = new PluginContext(Bots, BotContextRegistry, Options, LoggerFilter) { CancellationTokenLazy = new Lazy<CancellationToken>(() => CancellationTokenSourceLazy.Value.Token) };
	}

	public async Task<string?> OnBotCommand(Bot? bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		if (!Context.Valid) {
			CreateContext();
		}

		return await CommandDispatcher.Execute(bot, message, args, steamID, access).ConfigureAwait(false);
	}

	public async Task OnBotDestroy(Bot bot) => await RemoveBot(bot).ConfigureAwait(false);

	public async Task OnBotDisconnected(Bot bot, EResult reason) => await RemoveBot(bot).ConfigureAwait(false);

	public Task OnBotInit(Bot bot) => Task.CompletedTask;

	public async Task OnBotLoggedOn(Bot bot) => await RegisterBot(bot).ConfigureAwait(false);

	public Task OnLoaded() {
		LoggerFilter.InstallSensitiveConfigDumpFilter();

		if (VerboseLog) {
			ArchiLogger.LogGenericInfo($"Loaded {Name}");
		}

		return Task.CompletedTask;
	}

	public async Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		// Rename any files left on disk by the plugin's previous identity (ASFFreeGames) before options/state are loaded
		LegacyFilesMigrator.MigrateIfNeeded();

		ASFUnpaidStuffOptionsLoader.Bind(ref OptionsField);
		ASFUnpaidStuffOptionsLoader.BindFromASFJson(ref OptionsField, additionalConfigProperties);
		JsonElement? jsonElement = GlobalDatabase?.LoadFromJsonStorage($"{Name}.Verbose");

		if (jsonElement is null or { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null }) {
			// Fall back to the storage key written under the plugin's previous name
			jsonElement = GlobalDatabase?.LoadFromJsonStorage("ASFFreeGamesPlugin.Verbose");
		}

		if (jsonElement?.ValueKind is JsonValueKind.True) {
			Options.VerboseLog = true;
		}

		await SaveOptions(CancellationToken).ConfigureAwait(false);
	}

	public Task OnUpdateFinished(Version currentVersion, Version newVersion) => Task.CompletedTask;

	public Task OnUpdateProceeding(Version currentVersion, Version newVersion) => Task.CompletedTask;

	public async Task<uint> GetPreferredChangeNumberToStartFrom() => await SteamPackageDiscoveryService.GetPreferredChangeNumberToStartFrom(CancellationToken).ConfigureAwait(false);

	public async Task OnPICSChanges(uint currentChangeNumber, IReadOnlyDictionary<uint, SteamApps.PICSChangesCallback.PICSChangeData> appChanges, IReadOnlyDictionary<uint, SteamApps.PICSChangesCallback.PICSChangeData> packageChanges) =>
		await SteamPackageDiscoveryService.OnPICSChanges(currentChangeNumber, packageChanges, CancellationToken).ConfigureAwait(false);

	public async Task OnPICSChangesRestart(uint currentChangeNumber) {
		Bot? refreshBot = Bots.FirstOrDefault(static bot => bot.IsConnectedAndLoggedOn);

		await SteamPackageDiscoveryService.OnPICSChangesRestart(refreshBot, currentChangeNumber, CancellationToken).ConfigureAwait(false);
	}

	public async void CollectGamesOnClock(object? source) {
		try {
			CollectIntervalManager.RandomlyChangeCollectInterval(source);

			if (!Context.Valid || ((Bots.Count > 0) && (Context.Bots.Count != Bots.Count))) {
				CreateContext();
			}

			using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
			cts.CancelAfter(TimeSpan.FromMilliseconds(CollectGamesTimeout));

			if (cts.IsCancellationRequested || !Context.Valid) {
				return;
			}

			// ReSharper disable once AccessToDisposedClosure
			using (Context.TemporaryChangeCancellationToken(() => cts.Token)) {
				Bot[] reorderedBots;
				IContextRegistry botContexts = Context.BotContexts;

				lock (botContexts) {
					long orderByRunKeySelector(Bot bot) => botContexts.GetBotContext(bot)?.RunElapsedMilli ?? long.MaxValue;
					int comparison(Bot x, Bot y) => orderByRunKeySelector(y).CompareTo(orderByRunKeySelector(x)); // sort in descending order
					reorderedBots = Bots.ToArray();
					Array.Sort(reorderedBots, comparison);
				}

				if (reorderedBots.Length == 0) {
					ArchiLogger.LogGenericDebug("no viable bot found for freegame scheduled operation");

					return;
				}

				if (!cts.IsCancellationRequested) {
					string cmd = $"UNPAIDSTUFF {UnpaidStuffCommand.CollectInternalCommandString} " + string.Join(' ', reorderedBots.Select(static bot => bot.BotName));

					try {
						await OnBotCommand(null, EAccess.Owner, cmd, cmd.Split()).ConfigureAwait(false);
					}
					catch (Exception ex) {
						ArchiLogger.LogGenericWarning($"Failed to execute scheduled free games collection: {ex.Message}");
					}
				}
			}
		}
		catch (Exception ex) {
			ArchiLogger.LogGenericWarning($"Scheduled free games collection failed: {ex.Message}");
		}
	}

	private const int FreebieScanTimeout = 15 * 60 * 1000;

	/// <summary>
	/// Timer callback of <see cref="FreebieScanIntervalManager"/>: fires the internal freebie-scan
	/// command for all registered bots, mirroring how <see cref="CollectGamesOnClock"/> drives collection.
	/// </summary>
	public async void ScanFreebiesOnClock(object? source) {
		try {
			if (!Context.Valid || ((Bots.Count > 0) && (Context.Bots.Count != Bots.Count))) {
				CreateContext();
			}

			// Re-arm the timer with the currently configured interval so SET SCANINTERVAL applies live
			FreebieScanIntervalManager.RefreshInterval();

			if (Options.Freebies?.Enabled != true) {
				return;
			}

			using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
			cts.CancelAfter(TimeSpan.FromMilliseconds(FreebieScanTimeout));

			if (cts.IsCancellationRequested || !Context.Valid) {
				return;
			}

			// ReSharper disable once AccessToDisposedClosure
			using (Context.TemporaryChangeCancellationToken(() => cts.Token)) {
				Bot[] bots = Bots.ToArray();

				if (bots.Length == 0) {
					return;
				}

				string cmd = $"UNPAIDSTUFF {UnpaidStuffCommand.FreebiesInternalCommandString} " + string.Join(' ', bots.Select(static bot => bot.BotName));

				try {
					await OnBotCommand(null, EAccess.Owner, cmd, cmd.Split()).ConfigureAwait(false);
				}
				catch (Exception ex) {
					ArchiLogger.LogGenericWarning($"Failed to execute scheduled freebie scan: {ex.Message}");
				}
			}
		}
		catch (Exception ex) {
			ArchiLogger.LogGenericWarning($"Scheduled freebie scan failed: {ex.Message}");
		}
	}

	/// <summary>
	/// Creates a new PluginContext instance and assigns it to the Context property.
	/// </summary>
	private void CreateContext() => Context = new PluginContext(Bots, BotContextRegistry, Options, LoggerFilter, true) { CancellationTokenLazy = new Lazy<CancellationToken>(() => CancellationTokenSourceLazy.Value.Token) };

	// Makes the add-then-start and count-then-stop timer sequences atomic: per-bot callback loops invoke RegisterBot/RemoveBot concurrently
	private readonly object TimerLifecycleLock = new();

	private async Task RegisterBot(Bot bot) {
		lock (TimerLifecycleLock) {
			Bots.Add(bot);

			StartTimerIfNeeded();
		}

		await BotContextRegistry.SaveBotContext(bot, new BotContext(bot), CancellationToken).ConfigureAwait(false);
		BotContext? ctx = BotContextRegistry.GetBotContext(bot);

		if (ctx is not null) {
			await ctx.LoadFromFileSystem(CancellationToken).ConfigureAwait(false);
		}
	}

	private async Task RemoveBot(Bot bot) {
		Bots.Remove(bot);

		BotContext? botContext = BotContextRegistry.GetBotContext(bot);

		if (botContext is not null) {
			try {
				await botContext.SaveToFileSystem(CancellationToken).ConfigureAwait(false);
			}
			finally {
				await BotContextRegistry.RemoveBotContext(bot).ConfigureAwait(false);
				botContext.Dispose();
			}
		}

		lock (TimerLifecycleLock) {
			if (Bots.Count == 0) {
				CollectIntervalManager.StopTimer();
				FreebieScanIntervalManager.StopTimer();
			}
		}

		LoggerFilter.RemoveFilters(bot);
		BotPackageChecker.RemoveBotCache(bot);
	}

	// ReSharper disable once UnusedMethodReturnValue.Local
	private async Task<string?> SaveOptions(CancellationToken cancellationToken) {
		if (!cancellationToken.IsCancellationRequested) {
			const string cmd = $"UNPAIDSTUFF {UnpaidStuffCommand.SaveOptionsInternalCommandString}";
			async Task<string?> continuation() => await OnBotCommand(Bots.FirstOrDefault()!, EAccess.Owner, cmd, cmd.Split()).ConfigureAwait(false);

			string? result;

			if (Context.Valid) {
				using (Context.TemporaryChangeCancellationToken(() => cancellationToken)) {
					result = await continuation().ConfigureAwait(false);
				}
			}
			else {
				result = await continuation().ConfigureAwait(false);
			}

			return result;
		}

		return null;
	}

	private void StartTimerIfNeeded() {
		CollectIntervalManager.StartTimerIfNeeded();

		// Always armed: the tick itself checks Options.Freebies.Enabled, so SET FREEBIES can (re-)enable the scan without an ASF restart
		FreebieScanIntervalManager.StartTimerIfNeeded();
	}

	~ASFUnpaidStuffPlugin() {
		CollectIntervalManager.Dispose();
		FreebieScanIntervalManager.Dispose();
	}

	#region IGitHubPluginUpdates implementation
	private readonly GithubPluginUpdater Updater = new(new Lazy<Version>(GetVersion));
	string IGitHubPluginUpdates.RepositoryName => GithubPluginUpdater.RepositoryName;

	bool IGitHubPluginUpdates.CanUpdate => Updater.CanUpdate;

	Task<Uri?> IGitHubPluginUpdates.GetTargetReleaseURL(Version asfVersion, string asfVariant, bool asfUpdate, bool stable, bool forced) => Updater.GetTargetReleaseURL(asfVersion, asfVariant, asfUpdate, stable, forced);
	#endregion
}

#pragma warning restore CA1812 // ASF uses this class during runtime
