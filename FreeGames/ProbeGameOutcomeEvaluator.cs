namespace Maxisoft.ASF.FreeGames;

#nullable enable

/// <summary>
///     Pure decision logic that turns the raw signals collected while probing a candidate on the probe (separate)
///     account into a final <see cref="EProbeGameStatus" />. Kept side-effect free so it can be unit tested in isolation.
/// </summary>
internal static class ProbeGameOutcomeEvaluator {
	/// <summary>
	///     Evaluates whether a probed candidate should be considered validated for the Game Collector badge.
	/// </summary>
	/// <param name="requireBadgeIncrement">Whether the Game Collector badge "games owned" count must increase to validate.</param>
	/// <param name="before">Badge count read before the probe ADDLICENSE (null when <paramref name="requireBadgeIncrement" /> is disabled).</param>
	/// <param name="after">Badge count read after the probe ADDLICENSE (null when <paramref name="requireBadgeIncrement" /> is disabled).</param>
	/// <param name="ownsLicense">Whether the probe account is detected to own the license after the probe.</param>
	/// <returns><see cref="EProbeGameStatus.Validated" /> when the candidate counts toward the badge, otherwise <see cref="EProbeGameStatus.NotIncremented" />.</returns>
	internal static EProbeGameStatus Evaluate(bool requireBadgeIncrement, ulong? before, ulong? after, bool ownsLicense) {
		// A confirmed badge increment is itself proof that the probe account now owns the game, so it satisfies the
		// ownership requirement even when license detection is unreliable (notably for app/ identifiers, which ASF
		// does not expose through a queryable owned-apps list).
		bool badgeIncremented = requireBadgeIncrement && before.HasValue && after.HasValue && (after.Value > before.Value);

		if (!ownsLicense && !badgeIncremented) {
			return EProbeGameStatus.NotIncremented;
		}

		return !requireBadgeIncrement || badgeIncremented ? EProbeGameStatus.Validated : EProbeGameStatus.NotIncremented;
	}
}
