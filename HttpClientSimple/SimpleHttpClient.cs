using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Maxisoft.ASF.HttpClientSimple;

#nullable enable

public sealed class SimpleHttpClient : IDisposable {
	private readonly HttpMessageHandler HttpMessageHandler;
	private readonly HttpClient HttpClient;

	public SimpleHttpClient(IWebProxy? proxy = null, long timeout = 25_000) {
		SocketsHttpHandler handler = new();

		handler.AutomaticDecompression = DecompressionMethods.All;
		handler.MaxConnectionsPerServer = 5;
		handler.EnableMultipleHttp2Connections = true;

		if (proxy is not null) {
			handler.Proxy = proxy;
			handler.UseProxy = true;

			if (proxy.Credentials is not null) {
				handler.PreAuthenticate = true;
			}
		}

		HttpMessageHandler = handler;
#pragma warning disable CA5399
		HttpClient = new HttpClient(handler, false);
#pragma warning restore CA5399
		HttpClient.DefaultRequestVersion = HttpVersion.Version30;
		HttpClient.Timeout = TimeSpan.FromMilliseconds(timeout);

		HttpClient.DefaultRequestHeaders.ExpectContinue = false;

		HttpClient.DefaultRequestHeaders.Add("User-Agent", "Lynx/2.8.8dev.9 libwww-FM/2.14 SSL-MM/1.4.1 GNUTLS/2.12.14");
		HttpClient.DefaultRequestHeaders.Add("DNT", "1");
		HttpClient.DefaultRequestHeaders.Add("Sec-GPC", "1");

		HttpClient.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
		HttpClient.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en", 0.8));
	}

	public async Task<HttpStreamResponse> GetStreamAsync(Uri uri, IEnumerable<KeyValuePair<string, string>>? additionalHeaders = null, CancellationToken cancellationToken = default) {
		using HttpRequestMessage request = new(HttpMethod.Get, uri);
		request.Version = HttpClient.DefaultRequestVersion;

		// Add additional headers if provided
		if (additionalHeaders != null) {
			foreach (KeyValuePair<string, string> header in additionalHeaders) {
				request.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}
		}

		HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		Stream? stream = null;

		try {
			stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (!response.IsSuccessStatusCode && ex is HttpRequestException or IOException) {
			// non-success status: leave stream null and assume the caller checks the status code before reading the stream.
			// Other exception types (e.g. OperationCanceledException) and any failure on a success response are rethrown.
		}

		return new HttpStreamResponse(response, stream);
	}

	public void Dispose() {
		HttpClient.Dispose();
		HttpMessageHandler.Dispose();
	}
}

public sealed class HttpStreamResponse(HttpResponseMessage response, Stream? stream) : IAsyncDisposable {
	public HttpResponseMessage Response { get; } = response;
	public Stream Stream { get; } = stream ?? EmptyStreamLazy.Value;

	public bool HasValidStream => stream is not null && (!EmptyStreamLazy.IsValueCreated || !ReferenceEquals(EmptyStreamLazy.Value, Stream));

	public async Task<string> ReadAsStringAsync(CancellationToken cancellationToken) {
		using StreamReader reader = new(Stream); // assume the encoding is UTF8, cannot be specified as per issue #91

		return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
	}

	public HttpStatusCode StatusCode => Response.StatusCode;

	public async ValueTask DisposeAsync() {
		ValueTask task = HasValidStream ? Stream.DisposeAsync() : ValueTask.CompletedTask;
		Response.Dispose();
		await task.ConfigureAwait(false);
	}

	private static readonly Lazy<Stream> EmptyStreamLazy = new(static () => new MemoryStream([], false));
}
