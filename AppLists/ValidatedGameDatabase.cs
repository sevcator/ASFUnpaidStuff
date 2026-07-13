using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm;
using ASFUnpaidStuff.ASFExtensions.Games;
using Maxisoft.ASF.ASFExtensions.Games;

namespace Maxisoft.ASF.AppLists;

#nullable enable

internal sealed class ValidatedGameDatabase : IDisposable {
	public const string DefaultFileName = "unpaidstuff.validated.txt";

	private readonly HashSet<GameIdentifier> Entries = new();
	private readonly SemaphoreSlim Semaphore = new(1, 1);
	private bool Loaded;

	public ValidatedGameDatabase(string filePath) {
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

		FilePath = filePath;
	}

	public string FilePath { get; }

	public static ValidatedGameDatabase CreateDefault() => new(Path.Combine(SharedInfo.ConfigDirectory, DefaultFileName));

	public void Dispose() => Semaphore.Dispose();

	public async Task<bool> Contains(GameIdentifier gameIdentifier, CancellationToken cancellationToken = default) {
		await EnsureLoaded(cancellationToken).ConfigureAwait(false);

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			return Entries.Contains(gameIdentifier);
		}
		finally {
			Semaphore.Release();
		}
	}

	public async Task<IReadOnlyCollection<GameIdentifier>> Snapshot(CancellationToken cancellationToken = default) {
		await EnsureLoaded(cancellationToken).ConfigureAwait(false);

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			return Entries.ToArray();
		}
		finally {
			Semaphore.Release();
		}
	}

	public async Task<bool> TryAdd(GameIdentifier gameIdentifier, CancellationToken cancellationToken = default) {
		if (!IsSupported(gameIdentifier)) {
			return false;
		}

		await EnsureLoaded(cancellationToken).ConfigureAwait(false);

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			if (!Entries.Add(gameIdentifier)) {
				return false;
			}

			await SaveLoadedEntries(cancellationToken).ConfigureAwait(false);

			return true;
		}
		finally {
			Semaphore.Release();
		}
	}

	public async Task<int> TryAddMany(IEnumerable<GameIdentifier> gameIdentifiers, CancellationToken cancellationToken = default) {
		ArgumentNullException.ThrowIfNull(gameIdentifiers);

		GameIdentifier[] supportedGameIdentifiers = gameIdentifiers
			.Where(static gameIdentifier => IsSupported(gameIdentifier))
			.Distinct()
			.ToArray();

		if (supportedGameIdentifiers.Length == 0) {
			return 0;
		}

		await EnsureLoaded(cancellationToken).ConfigureAwait(false);

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			int added = 0;

			foreach (GameIdentifier gameIdentifier in supportedGameIdentifiers) {
				if (Entries.Add(gameIdentifier)) {
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

	public async Task Save(CancellationToken cancellationToken = default) {
		await EnsureLoaded(cancellationToken).ConfigureAwait(false);

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			await SaveLoadedEntries(cancellationToken).ConfigureAwait(false);
		}
		finally {
			Semaphore.Release();
		}
	}

	internal static bool TryParseLine(string? line, out GameIdentifier gameIdentifier) {
		gameIdentifier = default(GameIdentifier);

		if (string.IsNullOrWhiteSpace(line)) {
			return false;
		}

		string trimmed = line.Trim();

		if ((trimmed.Length < 3) || (trimmed[1] != '/') || ((char.ToUpperInvariant(trimmed[0]) != 'A') && (char.ToUpperInvariant(trimmed[0]) != 'S'))) {
			return false;
		}

		if (!GameIdentifier.TryParse(trimmed, out gameIdentifier) || !IsSupported(gameIdentifier)) {
			gameIdentifier = default(GameIdentifier);

			return false;
		}

		return true;
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
					if (TryParseLine(line, out GameIdentifier gameIdentifier)) {
						Entries.Add(gameIdentifier);
					}
				}
			}

			Loaded = true;
		}
		finally {
			Semaphore.Release();
		}
	}

	private static bool IsSupported(GameIdentifier gameIdentifier) => gameIdentifier.Valid && (gameIdentifier.Type is GameIdentifierType.App or GameIdentifierType.Sub);

	private async Task SaveLoadedEntries(CancellationToken cancellationToken) {
		string? directory = Path.GetDirectoryName(FilePath);

		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		string tempFilePath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

		try {
			IOrderedEnumerable<GameIdentifier> orderedEntries = Entries
				.Where(static entry => IsSupported(entry))
				.OrderBy(static entry => entry.Type)
				.ThenBy(static entry => entry.Id);

			await File.WriteAllLinesAsync(tempFilePath, orderedEntries.Select(static entry => entry.ToString()), cancellationToken).ConfigureAwait(false);
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
}
