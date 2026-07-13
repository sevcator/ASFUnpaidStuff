using System;
using System.Collections.Generic;
using System.Linq;
using ASFUnpaidStuff.ASFExtensions.Games;
using Maxisoft.ASF.ASFExtensions.Games;

namespace Maxisoft.ASF.SteamPackages;

internal static class SteamPackagePreFilter {
	internal static SteamPackagePreFilterResult Apply(IEnumerable<uint> pendingPackages, IReadOnlySet<uint> checkedPackages, IReadOnlySet<uint>? validatedPackages = null) {
		ArgumentNullException.ThrowIfNull(pendingPackages);
		ArgumentNullException.ThrowIfNull(checkedPackages);

		HashSet<uint> remaining = pendingPackages.Where(static packageId => packageId > 0).ToHashSet();
		HashSet<uint> validatedSkipped = validatedPackages is null ? [] : remaining.Where(validatedPackages.Contains).ToHashSet();
		HashSet<uint> checkedSkipped = remaining.Where(packageId => checkedPackages.Contains(packageId) && !validatedSkipped.Contains(packageId)).ToHashSet();

		remaining.ExceptWith(validatedSkipped);
		remaining.ExceptWith(checkedSkipped);

		GameIdentifier[] validatedCandidates = validatedSkipped
			.Select(static packageId => new GameIdentifier(packageId, GameIdentifierType.Sub))
			.ToArray();

		return new SteamPackagePreFilterResult(remaining, checkedSkipped, validatedSkipped, validatedCandidates);
	}
}

internal readonly record struct SteamPackagePreFilterResult(
	HashSet<uint> RemainingPackages,
	IReadOnlyCollection<uint> CheckedSkippedPackages,
	IReadOnlyCollection<uint> ValidatedSkippedPackages,
	IReadOnlyCollection<GameIdentifier> ValidatedCandidates
);
