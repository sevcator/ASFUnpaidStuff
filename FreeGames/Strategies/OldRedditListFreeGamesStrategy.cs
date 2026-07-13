using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Maxisoft.ASF.Reddit;

// ReSharper disable once CheckNamespace
namespace Maxisoft.ASF.FreeGames.Strategies;

[SuppressMessage("ReSharper", "RedundantNullableFlowAttribute")]
public sealed class OldRedditListFreeGamesStrategy : IListFreeGamesStrategy {
	public void Dispose() { }

	public async Task<IReadOnlyCollection<RedditGameEntry>> GetGames([NotNull] ListFreeGamesContext context, CancellationToken cancellationToken) {
		cancellationToken.ThrowIfCancellationRequested();

		return await OldRedditHelper.GetGames(context.HttpClient.Value, context.Options.Reddit, context.Retry, cancellationToken).ConfigureAwait(false);
	}
}
