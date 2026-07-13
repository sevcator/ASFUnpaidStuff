using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm;

namespace Maxisoft.ASF.SteamPackages;

#nullable enable

internal sealed class SteamPackageState : IDisposable {
	public const string DefaultFileName = "unpaidstuff.steam.state.cache";

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
	private readonly SemaphoreSlim Semaphore = new(1, 1);
	private StorageModel Storage = new();
	private bool Loaded;

	public SteamPackageState(string filePath) {
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

		FilePath = filePath;
	}

	public string FilePath { get; }

	public static SteamPackageState CreateDefault() => new(Path.Combine(SharedInfo.ConfigDirectory, DefaultFileName));

	public void Dispose() => Semaphore.Dispose();

	public async Task<uint> GetLastChangeNumber(CancellationToken cancellationToken = default) {
		await EnsureLoaded(cancellationToken).ConfigureAwait(false);

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			return Storage.LastChangeNumber;
		}
		finally {
			Semaphore.Release();
		}
	}

	public async Task<HashSet<uint>> GetPendingPackages(CancellationToken cancellationToken = default) {
		await EnsureLoaded(cancellationToken).ConfigureAwait(false);

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			return Storage.PendingPackages.Where(static packageId => packageId > 0).ToHashSet();
		}
		finally {
			Semaphore.Release();
		}
	}

	public async Task AddChanges(uint currentChangeNumber, IEnumerable<uint>? packageIds, CancellationToken cancellationToken = default) {
		await EnsureLoaded(cancellationToken).ConfigureAwait(false);

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			Storage.LastChangeNumber = Math.Max(Storage.LastChangeNumber, currentChangeNumber);

			if (packageIds is not null) {
				Storage.PendingPackages.UnionWith(packageIds.Where(static packageId => packageId > 0));
			}

			await SaveLoaded(cancellationToken).ConfigureAwait(false);
		}
		finally {
			Semaphore.Release();
		}
	}

	public async Task RemovePending(IEnumerable<uint> packageIds, CancellationToken cancellationToken = default) {
		await EnsureLoaded(cancellationToken).ConfigureAwait(false);

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			Storage.PendingPackages.ExceptWith(packageIds);
			await SaveLoaded(cancellationToken).ConfigureAwait(false);
		}
		finally {
			Semaphore.Release();
		}
	}

	public async Task UpdateLastChangeNumber(uint currentChangeNumber, CancellationToken cancellationToken = default) {
		await EnsureLoaded(cancellationToken).ConfigureAwait(false);

		await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		try {
			if (currentChangeNumber > Storage.LastChangeNumber) {
				Storage.LastChangeNumber = currentChangeNumber;
				await SaveLoaded(cancellationToken).ConfigureAwait(false);
			}
		}
		finally {
			Semaphore.Release();
		}
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

			if (File.Exists(FilePath)) {
				try {
					string json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
					Storage = string.IsNullOrWhiteSpace(json) ? new StorageModel() : JsonSerializer.Deserialize<StorageModel>(json) ?? new StorageModel();
				}
				catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
					ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarning($"[UnpaidStuff] Unable to load Steam package state: {e.Message}");
					Storage = new StorageModel();
				}
			}

			Storage.PendingPackages.RemoveWhere(static packageId => packageId == 0);
			Loaded = true;
		}
		finally {
			Semaphore.Release();
		}
	}

	private async Task SaveLoaded(CancellationToken cancellationToken) {
		string? directory = Path.GetDirectoryName(FilePath);

		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		string tempFilePath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try {
			string json = JsonSerializer.Serialize(Storage, JsonOptions);
			await File.WriteAllTextAsync(tempFilePath, json, cancellationToken).ConfigureAwait(false);
			File.Move(tempFilePath, FilePath, overwrite: true);
		}
		finally {
			if (File.Exists(tempFilePath)) {
				File.Delete(tempFilePath);
			}
		}
	}

	internal sealed class StorageModel {
		[JsonInclude]
		public uint LastChangeNumber { get; set; }

		[JsonInclude]
		public HashSet<uint> PendingPackages { get; set; } = [];
	}
}
