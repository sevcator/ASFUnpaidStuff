using System;
using System.Threading;

namespace Maxisoft.ASF;

/// <summary>
/// Owns the periodic timer that triggers the automatic freebie scan (sale-event items,
/// discovery queue, free point-shop items). Modeled after <see cref="CollectIntervalManager"/>
/// but without per-tick re-jitter: the scan is cooldown-guarded per bot, so a fixed cadence is enough.
/// </summary>
internal sealed class FreebieScanIntervalManager(IASFUnpaidStuffPlugin plugin) : IDisposable {
	private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
	private static readonly TimeSpan MinInterval = TimeSpan.FromHours(1);

	// System.Threading.Timer throws ArgumentOutOfRangeException above ~49.7 days; also nothing sensible scans less than weekly
	private static readonly TimeSpan MaxInterval = TimeSpan.FromHours(24 * 7);

	private readonly object LockObject = new();
	private Timer? Timer;

	public void StartTimerIfNeeded() {
		lock (LockObject) {
			if (Timer is not null) {
				return;
			}

			Timer = new Timer(plugin.ScanFreebiesOnClock, null, InitialDelay, GetInterval());
		}
	}

	/// <summary>
	/// Re-reads the configured interval and re-arms the running timer, so a SET SCANINTERVAL /
	/// RELOAD takes effect from the next tick without restarting ASF.
	/// </summary>
	public void RefreshInterval() {
		lock (LockObject) {
			if (Timer is not null) {
				TimeSpan interval = GetInterval();
				Timer.Change(interval, interval);
			}
		}
	}

	private TimeSpan GetInterval() {
		TimeSpan interval = plugin.Options.Freebies.ScanInterval;

		if (interval < MinInterval) {
			return MinInterval;
		}

		return interval > MaxInterval ? MaxInterval : interval;
	}

	public void StopTimer() {
		lock (LockObject) {
			Timer?.Dispose();
			Timer = null;
		}
	}

	public void Dispose() => StopTimer();
}
