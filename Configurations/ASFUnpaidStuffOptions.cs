using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using ArchiSteamFarm.Steam;
using ASFUnpaidStuff.ASFExtensions.Games;
using Maxisoft.ASF;
using Maxisoft.ASF.ASFExtensions;

namespace ASFUnpaidStuff.Configurations;

public class ASFUnpaidStuffOptions {
	[JsonPropertyName("sources")]
	public ASFUnpaidStuffSourceOptions Sources { get; set; } = new();

	[JsonPropertyName("steamDb")]
	public ASFUnpaidStuffSteamDbOptions SteamDb { get; set; } = new();

	[JsonPropertyName("steamStore")]
	public ASFUnpaidStuffSteamStoreOptions SteamStore { get; set; } = new();

	[JsonPropertyName("reddit")]
	public ASFUnpaidStuffRedditOptions Reddit { get; set; } = new();

	[JsonPropertyName("probe")]
	public ASFUnpaidStuffProbeOptions Probe { get; set; } = new();

	[JsonPropertyName("freebies")]
	public ASFUnpaidStuffFreebieOptions Freebies { get; set; } = new();

	// Use TimeSpan instead of long for representing time intervals
	[JsonPropertyName("recheckInterval")]
	public TimeSpan RecheckInterval { get; set; } = TimeSpan.FromMinutes(30);

	// Use Nullable<T> instead of bool? for nullable value types
	[JsonPropertyName("randomizeRecheckInterval")]
	public bool? RandomizeRecheckInterval { get; set; }

	[JsonPropertyName("skipFreeToPlay")]
	public bool? SkipFreeToPlay { get; set; }

	// ReSharper disable once InconsistentNaming
	[JsonPropertyName("skipDLC")]
	public bool? SkipDLC { get; set; }

	// Use IReadOnlyCollection<string> instead of HashSet<string> for blacklist property
	[JsonPropertyName("blacklist")]
	public IReadOnlyCollection<string> Blacklist { get; set; } = new HashSet<string>();

	[JsonPropertyName("verboseLog")]
	public bool? VerboseLog { get; set; }

	[JsonPropertyName("probeBotName")]
	public string? ProbeBotName { get; set; }

	#region IsBlacklisted
	public bool IsBlacklisted(in GameIdentifier gid) {
		if (Blacklist.Count <= 0) {
			return false;
		}

		return Blacklist.Contains(gid.ToString()) || Blacklist.Contains(gid.Id.ToString(CultureInfo.InvariantCulture));
	}

	public bool IsBlacklisted(in Bot? bot) => bot is null || ((Blacklist.Count > 0) && Blacklist.Contains($"bot/{bot.BotName}"));
	#endregion

	#region proxy
	[JsonPropertyName("proxy")]
	public string? Proxy { get; set; }

	[JsonPropertyName("redditProxy")]
	public string? RedditProxy { get; set; }

	[JsonPropertyName("redlibProxy")]
	public string? RedlibProxy { get; set; }
	#endregion

	[JsonPropertyName("redlibInstanceUrl")]
#pragma warning disable CA1056
	public string? RedlibInstanceUrl { get; set; } = "https://raw.githubusercontent.com/redlib-org/redlib-instances/main/instances.json";
#pragma warning restore CA1056
}

public sealed class ASFUnpaidStuffSourceOptions {
	[JsonPropertyName("steamPics")]
	public bool SteamPics { get; set; } = true;

	[JsonPropertyName("steamDbFreeToKeep")]
	public bool SteamDbFreeToKeep { get; set; } = true;

	[JsonPropertyName("redditAsfInfo")]
	public bool RedditAsfInfo { get; set; } = true;

	[JsonPropertyName("steamStoreProfileCheck")]
	public bool SteamStoreProfileCheck { get; set; } = true;
}

public sealed class ASFUnpaidStuffSteamDbOptions {
	[JsonPropertyName("url")]
#pragma warning disable CA1056
	public string? Url { get; set; } = "https://steamdb.info/freepackages/";
#pragma warning restore CA1056

	[JsonPropertyName("importPaths")]
	public IReadOnlyCollection<string> ImportPaths { get; set; } = new HashSet<string>();

	[JsonPropertyName("requireDiscountPercent")]
	public int RequireDiscountPercent { get; set; } = 100;

	[JsonPropertyName("maxPackages")]
	public int MaxPackages { get; set; } = 8192;

	[JsonPropertyName("cacheTtl")]
	public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(12);
}

public sealed class ASFUnpaidStuffSteamStoreOptions {
	[JsonPropertyName("rejectProfileFeaturesLimited")]
	public bool RejectProfileFeaturesLimited { get; set; } = true;

	[JsonPropertyName("rejectUnknownProfileFeatures")]
	public bool RejectUnknownProfileFeatures { get; set; } = true;

	[JsonPropertyName("maxParallelRequests")]
	public int MaxParallelRequests { get; set; } = 4;

	[JsonPropertyName("cacheTtl")]
	public TimeSpan CacheTtl { get; set; } = TimeSpan.FromDays(7);
}

public sealed class ASFUnpaidStuffRedditOptions {
	[JsonPropertyName("maxAgeDays")]
	public int? MaxAgeDays { get; set; }

	[JsonPropertyName("maxPages")]
	public int MaxPages { get; set; } = 100;

	[JsonPropertyName("pageLimit")]
	public int PageLimit { get; set; } = 100;

	[JsonPropertyName("maxEntries")]
	public int MaxEntries { get; set; } = 8192;
}

public sealed class ASFUnpaidStuffProbeOptions {
	[JsonPropertyName("requireAccountLicense")]
	public bool RequireAccountLicense { get; set; } = true;

	[JsonPropertyName("requireBadgeIncrement")]
	public bool RequireBadgeIncrement { get; set; } = true;
}

/// <summary>
/// Options of the automatic freebie scanner: sale-event item claims (stickers/cards handed out
/// during Steam sales), discovery-queue exploration and zero-cost point-shop giveaways.
/// </summary>
public sealed class ASFUnpaidStuffFreebieOptions {
	[JsonPropertyName("enabled")]
	public bool Enabled { get; set; } = true;

	[JsonPropertyName("claimSaleEventItems")]
	public bool ClaimSaleEventItems { get; set; } = true;

	[JsonPropertyName("claimFreePointShopItems")]
	public bool ClaimFreePointShopItems { get; set; } = true;

	[JsonPropertyName("exploreDiscoveryQueue")]
	public bool ExploreDiscoveryQueue { get; set; } = true;

	// Steam grants sale-card drops for up to 3 cleared queues per day during discovery-queue events
	[JsonPropertyName("discoveryQueuePasses")]
	public int DiscoveryQueuePasses { get; set; } = 3;

	[JsonPropertyName("scanInterval")]
	public TimeSpan ScanInterval { get; set; } = TimeSpan.FromHours(6);
}
