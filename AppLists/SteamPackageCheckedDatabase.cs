using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm;

namespace Maxisoft.ASF.AppLists;

#nullable enable

internal sealed class SteamPackageCheckedDatabase : IDisposable {
	private readonly HashSet<uint> Entries = new();
	private readonly SemaphoreSlim Semaphore = new(1, 1);
	private bool Loaded;

	public SteamPackageCheckedDatabase(string filePath) {
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

		FilePath = filePath;
	}

	public string FilePath { get; }

	public static SteamPackageCheckedDatabase CreateForProbeBot(string probeBotName) {
		ArgumentException.ThrowIfNullOrWhiteSpace(probeBotName);

		string sanitizedProbeBotName = SanitizeFileName(probeBotName.Trim());

		return new SteamPackageCheckedDatabase(Path.Combine(SharedInfo.ConfigDirectory, $"unpaidstuff.steam.checked.{sanitizedProbeBotName}.txt"));
	}

	public void Dispose() => Semaphore.Dispose();

	public async Task<bool> Contains(uint packageId, CancellationToken cancellationToken = default) {
		await EnsureLoaded(cancellationToken).ConfigureAwait(false);

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			return Entries.Contains(packageId);
		}
		finally {
			Semaphore.Release();
		}
	}

	public async Task<IReadOnlyCollection<uint>> Snapshot(CancellationToken cancellationToken = default) {
		await EnsureLoaded(cancellationToken).ConfigureAwait(false);

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			return Entries.ToArray();
		}
		finally {
			Semaphore.Release();
		}
	}

	public async Task<bool> TryAdd(uint packageId, CancellationToken cancellationToken = default) {
		if (packageId == 0) {
			return false;
		}

		await EnsureLoaded(cancellationToken).ConfigureAwait(false);

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			if (!Entries.Add(packageId)) {
				return false;
			}

			// Append the single new entry instead of rewriting the whole (potentially huge) file each call.
			// Order does not matter on disk; EnsureLoaded dedups on reload and the bulk path still compacts/sorts.
			await AppendEntry(packageId, cancellationToken).ConfigureAwait(false);

			return true;
		}
		finally {
			Semaphore.Release();
		}
	}

	public async Task<int> TryAddMany(IEnumerable<uint> packageIds, CancellationToken cancellationToken = default) {
		ArgumentNullException.ThrowIfNull(packageIds);

		uint[] normalizedPackageIds = packageIds
			.Where(static packageId => packageId > 0)
			.Distinct()
			.ToArray();

		if (normalizedPackageIds.Length == 0) {
			return 0;
		}

		await EnsureLoaded(cancellationToken).ConfigureAwait(false);

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			int added = 0;

			foreach (uint packageId in normalizedPackageIds) {
				if (Entries.Add(packageId)) {
					added++;
				}
			}

			if (added > 0) {
				await SaveLoadedEntries(cancellationToken).ConfigureAwait(false);
			}

			return added;
		}
		finally {
			Semaphore.Release();
		}
	}

	internal static bool TryParseLine(string? line, out uint packageId) {
		packageId = 0;

		if (string.IsNullOrWhiteSpace(line)) {
			return false;
		}

		string trimmed = line.Trim();

		if ((trimmed.Length < 3) || !trimmed.StartsWith("s/", StringComparison.OrdinalIgnoreCase)) {
			return false;
		}

		return uint.TryParse(trimmed[2..], NumberStyles.None, CultureInfo.InvariantCulture, out packageId) && (packageId > 0);
	}

	private async Task EnsureLoaded(CancellationToken cancellationToken) {
		if (Loaded) {
			return;
		}

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			if (Loaded) {
				return;
			}

			Entries.Clear();

			if (File.Exists(FilePath)) {
				string[] lines = await File.ReadAllLinesAsync(FilePath, cancellationToken).ConfigureAwait(false);

				foreach (string line in lines) {
					if (TryParseLine(line, out uint packageId)) {
						Entries.Add(packageId);
					}
				}
			}

			Loaded = true;
		}
		finally {
			Semaphore.Release();
		}
	}

	private async Task AppendEntry(uint packageId, CancellationToken cancellationToken) {
		string? directory = Path.GetDirectoryName(FilePath);

		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		await File.AppendAllTextAsync(FilePath, $"s/{packageId}{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
	}

	private async Task SaveLoadedEntries(CancellationToken cancellationToken) {
		string? directory = Path.GetDirectoryName(FilePath);

		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		string tempFilePath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

		try {
			await File.WriteAllLinesAsync(tempFilePath, Entries.Order().Select(static packageId => $"s/{packageId}"), cancellationToken).ConfigureAwait(false);
			File.Move(tempFilePath, FilePath, overwrite: true);
		}
		finally {
			try {
				if (File.Exists(tempFilePath)) {
					File.Delete(tempFilePath);
				}
			}
			catch (IOException) {
				// best-effort cleanup; do not mask the primary exception
			}
		}
	}

	private static readonly System.Buffers.SearchValues<char> InvalidFileNameChars = System.Buffers.SearchValues.Create(Path.GetInvalidFileNameChars());

	private static string SanitizeFileName(string value) => string.Create(value.Length, value, static (span, source) => {
		for (int i = 0; i < source.Length; i++) {
			span[i] = InvalidFileNameChars.Contains(source[i]) ? '_' : source[i];
		}
	});
}
