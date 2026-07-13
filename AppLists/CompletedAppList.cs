using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ASFUnpaidStuff.ASFExtensions.Games;
using Maxisoft.ASF.ASFExtensions;

namespace Maxisoft.ASF.AppLists;

internal sealed class CompletedAppList : IDisposable {
	internal long[]? CompletedAppBuffer { get; private set; }
	internal const int CompletedAppBufferSize = 8192;
	internal Memory<long> CompletedAppMemory => ((Memory<long>) CompletedAppBuffer!)[..CompletedAppBufferSize];
	internal RecentGameMapping CompletedApps { get; }
	internal const int FileCompletedAppBufferSize = CompletedAppBufferSize * sizeof(long) * 2;
	private static readonly ArrayPool<long> LongMemoryPool = ArrayPool<long>.Create(CompletedAppBufferSize, 10);
	private static readonly char Endianness = BitConverter.IsLittleEndian ? 'l' : 'b';
	public static readonly string FileExtension = $".fg{Endianness}dict";

	public CompletedAppList() {
		CompletedAppBuffer = LongMemoryPool.Rent(CompletedAppBufferSize);
		CompletedApps = new RecentGameMapping(CompletedAppMemory);
	}

	~CompletedAppList() => ReturnBuffer();

	private bool ReturnBuffer() {
		if (CompletedAppBuffer is { Length: > 0 }) {
			LongMemoryPool.Return(CompletedAppBuffer);

			return true;
		}

		return false;
	}

	public void Dispose() {
		if (ReturnBuffer()) {
			CompletedAppBuffer = Array.Empty<long>();
		}

		GC.SuppressFinalize(this);
	}

	public bool Add(in GameIdentifier gameIdentifier) => CompletedApps.Add(in gameIdentifier);
	public bool AddInvalid(in GameIdentifier gameIdentifier) => CompletedApps.AddInvalid(in gameIdentifier);

	public bool Contains(in GameIdentifier gameIdentifier) => CompletedApps.Contains(in gameIdentifier);

	public bool ContainsInvalid(in GameIdentifier gameIdentifier) => CompletedApps.ContainsInvalid(in gameIdentifier);
}

public static class CompletedAppListSerializer {
	[SuppressMessage("Code", "CAC001:ConfigureAwaitChecker")]
	internal static async Task SaveToFile(this CompletedAppList appList, string filePath, CancellationToken cancellationToken = default) {
		if (string.IsNullOrWhiteSpace(filePath)) {
			return;
		}
#pragma warning disable CA2007
		await using FileStream sourceStream = new(
			filePath,
			FileMode.Create, FileAccess.Write, FileShare.None,
			bufferSize: CompletedAppList.FileCompletedAppBufferSize, useAsync: true
		);

		await using BrotliStream encoder = new(sourceStream, CompressionMode.Compress);

		ChangeBrotliEncoderToFastCompress(encoder);
#pragma warning restore CA2007

		// note: cannot use WriteAsync call due to span & async incompatibilities
		// but it shouldn't be an issue as we use a bigger bufferSize than the written payload
		encoder.Write(MemoryMarshal.Cast<long, byte>(appList.CompletedAppMemory.Span));

		// Flush/FlushAsync does NOT emit the final Brotli frame - only Dispose does. Dispose the encoder explicitly
		// (which also disposes sourceStream) so the trailing block frame is written before the file handle closes,
		// instead of relying on the scope-exit ordering of two different using forms.
		await encoder.DisposeAsync().ConfigureAwait(false);
	}

	[SuppressMessage("Code", "CAC001:ConfigureAwaitChecker")]
	internal static async Task<bool> LoadFromFile(this CompletedAppList appList, string filePath, CancellationToken cancellationToken = default) {
		if (string.IsNullOrWhiteSpace(filePath)) {
			return false;
		}

		try {
#pragma warning disable CA2007
			await using FileStream sourceStream = new(
				filePath,
				FileMode.Open, FileAccess.Read, FileShare.Read,
				bufferSize: CompletedAppList.FileCompletedAppBufferSize, useAsync: true
			);

			// ReSharper disable once UseAwaitUsing
			using BrotliStream decoder = new(sourceStream, CompressionMode.Decompress);
#pragma warning restore CA2007
			// ReSharper disable once UseAwaitUsing
			using MemoryStream ms = new();
			await decoder.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);

			int targetLength = appList.CompletedAppMemory.Length * sizeof(long);

			if (appList.CompletedAppBuffer is { Length: > 0 } && (ms.Length > 0) && (ms.Length <= targetLength) && (ms.Length % sizeof(long) == 0)) {
				ms.Seek(0, SeekOrigin.Begin);
				Span<byte> target = MemoryMarshal.Cast<long, byte>(appList.CompletedAppMemory.Span);
				target.Clear();
				int size = ms.Read(target[..(int) ms.Length]);

				if (size != ms.Length) {
					ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericError("[UnpaidStuff] Unable to load previous completed app dict", nameof(LoadFromFile));
				}

				try {
					appList.CompletedApps.Reload();
				}
				catch (InvalidDataException e) {
					ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarningException(e, $"[UnpaidStuff] {nameof(appList.CompletedApps)}.{nameof(appList.CompletedApps.Reload)}");
					appList.CompletedApps.Reload(true);

					return false;
				}
			}
			else {
				ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericError("[UnpaidStuff] Unable to load previous completed app dict", nameof(LoadFromFile));
			}

			return true;
		}
		catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) {
			return false;
		}
	}

	/// <summary>
	/// Workaround in order to set brotli's compression level to fastest.
	/// Uses reflexions as the public methods got removed in the ASF public binary.
	/// </summary>
	/// <param name="encoder"></param>
	/// <param name="level"></param>
	private static void ChangeBrotliEncoderToFastCompress(BrotliStream encoder, int level = 1) {
		try {
			FieldInfo? field = encoder.GetType().GetField("_encoder", BindingFlags.NonPublic | BindingFlags.Instance);

			if (field?.GetValue(encoder) is BrotliEncoder previous) {
				BrotliEncoder brotliEncoder = default(BrotliEncoder);

				try {
					MethodInfo? method = brotliEncoder.GetType().GetMethod("SetQuality", BindingFlags.NonPublic | BindingFlags.Instance);
					method?.Invoke(brotliEncoder, new object?[] { level });
					field.SetValue(encoder, brotliEncoder);
				}
				catch (Exception) {
					brotliEncoder.Dispose();

					throw;
				}

				previous.Dispose();
			}
		}
		catch (Exception e) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericDebuggingException(e, nameof(ChangeBrotliEncoderToFastCompress));
		}
	}
}
