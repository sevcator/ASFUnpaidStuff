using System;
using ASFUnpaidStuff.Configurations;
using Maxisoft.ASF.HttpClientSimple;

// ReSharper disable once CheckNamespace
namespace Maxisoft.ASF.FreeGames.Strategies;

public sealed record ListFreeGamesContext(ASFUnpaidStuffOptions Options, Lazy<SimpleHttpClient> HttpClient, uint Retry = 5) {
	public required SimpleHttpClientFactory HttpClientFactory { get; init; }
	public EListFreeGamesStrategy PreviousSucessfulStrategy { get; set; }
}
