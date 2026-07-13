using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

#nullable enable
namespace ASFUnpaidStuff.Configurations;

public static class ASFUnpaidStuffOptionsSaver {
	private static readonly JsonSerializerOptions JsonOptions = new() {
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	public static async Task<int> SaveOptions([NotNull] Stream stream, [NotNull] ASFUnpaidStuffOptions options, bool checkValid = true, CancellationToken cancellationToken = default) {
		byte[] buffer = JsonSerializer.SerializeToUtf8Bytes(options, JsonOptions);

		if (checkValid) {
			PseudoValidate(buffer);
		}

		await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

		return buffer.Length;
	}

	private static void PseudoValidate(byte[] buffer) {
		ASFUnpaidStuffOptions? options = JsonSerializer.Deserialize<ASFUnpaidStuffOptions>(buffer, JsonOptions);

		if (options is null) {
			throw new InvalidOperationException("Unable to validate serialized options");
		}
	}
}
