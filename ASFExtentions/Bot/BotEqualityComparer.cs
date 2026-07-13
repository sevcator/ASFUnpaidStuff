using System;
using System.Collections.Generic;

namespace ASFUnpaidStuff.ASFExtensions.Bot;

using Bot = ArchiSteamFarm.Steam.Bot;

internal sealed class BotEqualityComparer : IEqualityComparer<Bot> {
	public bool Equals(Bot? x, Bot? y) {
		if (ReferenceEquals(x, y)) {
			return true;
		}

		if (ReferenceEquals(x, null)) {
			return false;
		}

		if (ReferenceEquals(y, null)) {
			return false;
		}

		return string.Equals(x.BotName, y.BotName, StringComparison.OrdinalIgnoreCase) && (x.SteamID == y.SteamID);
	}

	public int GetHashCode(Bot? obj) => obj != null ? HashCode.Combine(obj.BotName.GetHashCode(StringComparison.OrdinalIgnoreCase), obj.SteamID) : 0;
}
