using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm;
using ASFUnpaidStuff.Commands.GetIp;
using ASFUnpaidStuff.Configurations;
using Microsoft.Extensions.Configuration;

namespace Maxisoft.ASF.Configurations;

public static class ASFUnpaidStuffOptionsLoader {
	public static void Bind(ref ASFUnpaidStuffOptions options) {
		// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
		options ??= new ASFUnpaidStuffOptions();
		Semaphore.Wait();

		try {
			IConfigurationRoot configurationRoot = CreateConfigurationRoot();
			BindFromConfiguration(options, configurationRoot);
		}
		finally {
			Semaphore.Release();
		}
	}

	public static void BindFromASFJson(ref ASFUnpaidStuffOptions options, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties) {
		options ??= new ASFUnpaidStuffOptions();

		if (additionalConfigProperties is null) {
			return;
		}

		if (!TryGetPluginConfig(additionalConfigProperties, out JsonElement pluginConfig) || pluginConfig.ValueKind is not JsonValueKind.Object) {
			return;
		}

		using MemoryStream jsonStream = new(Encoding.UTF8.GetBytes(pluginConfig.GetRawText()));
		IConfigurationRoot configurationRoot = new ConfigurationBuilder()
			.AddJsonStream(jsonStream)
			.Build();

		BindFromConfiguration(options, configurationRoot);
	}

	private static IConfigurationRoot CreateConfigurationRoot() {
		IConfigurationRoot configurationRoot = new ConfigurationBuilder()
			.SetBasePath(Path.GetFullPath(BasePath))
			.AddJsonFile(DefaultJsonFile, true, false)

			// Legacy prefix from the ASFFreeGames era is still honored; the new prefix is added last so it wins on conflicts
			.AddEnvironmentVariables("FREEGAMES_")
			.AddEnvironmentVariables("UNPAIDSTUFF_")
			.Build();

		return configurationRoot;
	}

	private static readonly SemaphoreSlim Semaphore = new(1, 1);

	private static string? NormalizeString(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	private static void BindFromConfiguration(ASFUnpaidStuffOptions options, IConfiguration configuration) {
		configuration.Bind(options);

		if (configuration.GetValue<double?>("RecheckIntervalMs") is double recheckIntervalMs) {
			options.RecheckInterval = TimeSpan.FromMilliseconds(recheckIntervalMs);
		}

		options.Sources ??= new ASFUnpaidStuffSourceOptions();
		options.SteamDb ??= new ASFUnpaidStuffSteamDbOptions();
		options.SteamStore ??= new ASFUnpaidStuffSteamStoreOptions();
		options.Reddit ??= new ASFUnpaidStuffRedditOptions();
		options.Probe ??= new ASFUnpaidStuffProbeOptions();
		options.Freebies ??= new ASFUnpaidStuffFreebieOptions();

		options.Blacklist = new HashSet<string>(options.Blacklist ?? Enumerable.Empty<string>(), StringComparer.InvariantCultureIgnoreCase);
		options.SteamDb.ImportPaths = new HashSet<string>(options.SteamDb.ImportPaths ?? Enumerable.Empty<string>(), StringComparer.InvariantCultureIgnoreCase);
		options.ProbeBotName = NormalizeString(options.ProbeBotName);
		options.Proxy = NormalizeString(options.Proxy);
		options.RedditProxy = NormalizeString(options.RedditProxy);
		options.RedlibProxy = NormalizeString(options.RedlibProxy);
		options.RedlibInstanceUrl = NormalizeString(options.RedlibInstanceUrl);
		options.SteamDb.Url = NormalizeString(options.SteamDb.Url);

		if (options.SteamDb.RequireDiscountPercent <= 0) {
			options.SteamDb.RequireDiscountPercent = 100;
		}

		if (options.SteamDb.MaxPackages <= 0) {
			options.SteamDb.MaxPackages = 8192;
		}

		if (options.SteamDb.CacheTtl < TimeSpan.Zero) {
			options.SteamDb.CacheTtl = TimeSpan.Zero;
		}

		if (options.SteamStore.MaxParallelRequests <= 0) {
			options.SteamStore.MaxParallelRequests = 1;
		}

		if (options.SteamStore.CacheTtl < TimeSpan.Zero) {
			options.SteamStore.CacheTtl = TimeSpan.Zero;
		}

		if (options.Reddit.MaxPages <= 0) {
			options.Reddit.MaxPages = 1;
		}

		if (options.Reddit.PageLimit <= 0) {
			options.Reddit.PageLimit = 100;
		}

		if (options.Reddit.MaxEntries <= 0) {
			options.Reddit.MaxEntries = 8192;
		}

		if (options.Reddit.MaxAgeDays is < 0) {
			options.Reddit.MaxAgeDays = null;
		}

		options.Freebies.DiscoveryQueuePasses = Math.Clamp(options.Freebies.DiscoveryQueuePasses, 1, 5);

		if (options.Freebies.ScanInterval < TimeSpan.FromHours(1)) {
			options.Freebies.ScanInterval = TimeSpan.FromHours(1);
		}
		else if (options.Freebies.ScanInterval > TimeSpan.FromHours(24 * 7)) {
			// Also keeps System.Threading.Timer happy — it rejects periods above ~49.7 days
			options.Freebies.ScanInterval = TimeSpan.FromHours(24 * 7);
		}
	}

	// New keys take precedence; the ASFFreeGames-era keys remain accepted so existing ASF.json configs keep working
	private static readonly string[] PluginConfigKeys = ["ASFUnpaidStuff", "ASFUnpaidStuffPlugin", "ASFFreeGames", "ASFFreeGamesPlugin"];

	private static bool TryGetPluginConfig(IReadOnlyDictionary<string, JsonElement> additionalConfigProperties, out JsonElement pluginConfig) {
		// Interleave exact and case-insensitive lookup per key, so any spelling of a new key outranks any legacy key
		foreach (string configKey in PluginConfigKeys) {
			if (additionalConfigProperties.TryGetValue(configKey, out pluginConfig)) {
				return true;
			}

			foreach ((string key, JsonElement value) in additionalConfigProperties) {
				if (key.Equals(configKey, StringComparison.OrdinalIgnoreCase)) {
					pluginConfig = value;

					return true;
				}
			}
		}

		pluginConfig = default(JsonElement);

		return false;
	}

	public static async Task Save(ASFUnpaidStuffOptions options, CancellationToken cancellationToken) {
		string path = Path.Combine(BasePath, DefaultJsonFile);

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
#pragma warning disable CAC001
#pragma warning disable CA2007
			await using FileStream fs = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
#pragma warning restore CA2007
#pragma warning restore CAC001
			byte[] buffer = new byte[fs.Length > 0 ? (int) fs.Length + 1 : 1 << 15];

			int read = await fs.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

			try {
				fs.Position = 0;
				fs.SetLength(0);
				int written = await ASFUnpaidStuffOptionsSaver.SaveOptions(fs, options, true, cancellationToken).ConfigureAwait(false);
				fs.SetLength(written);
			}

			catch (Exception) {
				fs.Position = 0;

				await fs.WriteAsync(((ReadOnlyMemory<byte>) buffer)[..read], cancellationToken).ConfigureAwait(false);
				fs.SetLength(read);

				throw;
			}
		}
		finally {
			Semaphore.Release();
		}
	}

	public static string BasePath => SharedInfo.ConfigDirectory;
	public const string DefaultJsonFile = "unpaidstuff.json.config";
}
