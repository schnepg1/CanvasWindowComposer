using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasDesktop.Dictation;

/// <summary>HTTP + SSE client for the local transcription server.</summary>
public sealed class LocalTranscriptionClient : IDictationClient
{
    private const int MaxErrorBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly bool _disposeClient;
    private bool _disposed;

    public string BaseUrl { get; }

    public LocalTranscriptionClient(string? baseUrl = null)
    {
        BaseUrl = DictationDefaults.NormalizeOrDefault(baseUrl);
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.None
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _disposeClient = true;
    }

    public LocalTranscriptionClient(HttpMessageHandler handler, string? baseUrl = null, bool disposeHandler = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        BaseUrl = DictationDefaults.NormalizeOrDefault(baseUrl);
        _http = new HttpClient(handler, disposeHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _disposeClient = true;
    }

    public async Task<DictationHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = new HttpRequestMessage(HttpMethod.Get, EndpointUri("/health"));
        using var timeoutCts = CreateTimeoutCts(DictationDefaults.HealthTimeout, cancellationToken);

        using var response = await SendCoreAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, timeoutCts.Token).ConfigureAwait(false);
        return await ReadJsonAsync<DictationHealthResponse>(response, timeoutCts.Token).ConfigureAwait(false);
    }

    public async Task<IDictationStream> OpenStreamAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = new HttpRequestMessage(HttpMethod.Get, EndpointUri("/recording/stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var connectCts = CreateTimeoutCts(DictationDefaults.StreamConnectTimeout, cancellationToken);
        HttpResponseMessage response = await SendCoreAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await EnsureSuccessAsync(response, connectCts.Token).ConfigureAwait(false);
            Stream contentStream = await response.Content.ReadAsStreamAsync(connectCts.Token).ConfigureAwait(false);
            return new SseDictationStream(response, contentStream);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<DictationStartResponse> StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string payload = JsonSerializer.Serialize(new DictationStartRequest("mic"), JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, EndpointUri("/recording/start"))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var timeoutCts = CreateTimeoutCts(DictationDefaults.StartTimeout, cancellationToken);
        using var response = await SendCoreAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, timeoutCts.Token).ConfigureAwait(false);
        return await ReadJsonAsync<DictationStartResponse>(response, timeoutCts.Token).ConfigureAwait(false);
    }

    public async Task<DictationStopResponse> StopRecordingAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = new HttpRequestMessage(HttpMethod.Post, EndpointUri("/recording/stop"));
        using var timeoutCts = CreateTimeoutCts(timeout ?? DictationDefaults.StopTimeout, cancellationToken);

        using var response = await SendCoreAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, timeoutCts.Token).ConfigureAwait(false);
        return await ReadJsonAsync<DictationStopResponse>(response, timeoutCts.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_disposeClient)
            _http.Dispose();
    }

    private Uri EndpointUri(string relativePath)
        => new(BaseUrl + relativePath, UriKind.Absolute);

    private static CancellationTokenSource CreateTimeoutCts(TimeSpan timeout, CancellationToken callerToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        cts.CancelAfter(timeout);
        return cts;
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken effectiveToken,
        CancellationToken callerToken)
    {
        try
        {
            return await _http.SendAsync(request, completionOption, effectiveToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!callerToken.IsCancellationRequested)
        {
            throw new DictationClientException(
                DictationErrorKind.ServerUnreachable,
                DictationDefaults.UnreachableMessage(BaseUrl),
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DictationClientException(
                DictationErrorKind.ServerUnreachable,
                DictationDefaults.UnreachableMessage(BaseUrl),
                innerException: ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        int statusCode = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw new DictationClientException(
                DictationErrorKind.ModelLoading,
                DictationDefaults.ModelLoadingMessage,
                statusCode);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new DictationClientException(
                DictationErrorKind.AlreadyRecording,
                DictationDefaults.AlreadyRecordingMessage,
                statusCode);
        }

        string? detail = await TryReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
        string message = !string.IsNullOrWhiteSpace(detail)
            ? detail
            : $"Transcription server returned HTTP {statusCode}.";
        throw new DictationClientException(DictationErrorKind.ServerError, message, statusCode);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var parsed = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (parsed is null)
            {
                throw new DictationClientException(
                    DictationErrorKind.InvalidResponse,
                    "Transcription server returned an empty response.",
                    (int)response.StatusCode);
            }
            return parsed;
        }
        catch (JsonException ex)
        {
            throw new DictationClientException(
                DictationErrorKind.InvalidResponse,
                "Transcription server returned malformed JSON.",
                (int)response.StatusCode,
                ex);
        }
    }

    private static async Task<string?> TryReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var memory = new MemoryStream();
            byte[] buffer = new byte[4096];
            int total = 0;
            while (total < MaxErrorBodyBytes)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxErrorBodyBytes - total)), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0) break;
                memory.Write(buffer, 0, read);
                total += read;
            }

            if (total == 0) return null;
            string body = Encoding.UTF8.GetString(memory.GetBuffer(), 0, total).Trim();
            if (body.Length == 0) return null;

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if ((property.Name.Equals("detail", StringComparison.OrdinalIgnoreCase)
                         || property.Name.Equals("error", StringComparison.OrdinalIgnoreCase)
                         || property.Name.Equals("message", StringComparison.OrdinalIgnoreCase))
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        string? text = property.Value.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(text))
                            return text;
                    }
                }
            }
        }
        catch
        {
            // Error body parsing is best-effort only.
        }
        return null;
    }

    private sealed class SseDictationStream(HttpResponseMessage response, Stream stream) : IDictationStream
    {
        private int _disposed;

        public async IAsyncEnumerable<DictationStreamEvent> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (string rawData in SseEventParser.ReadDataEventsAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(rawData))
                    continue;

                DictationStreamEvent? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<DictationStreamEvent>(rawData, JsonOptions);
                }
                catch (JsonException)
                {
                    // Ignore malformed JSON frames without tearing down the live stream.
                    continue;
                }

                if (parsed is not null)
                    yield return parsed;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { await stream.DisposeAsync().ConfigureAwait(false); }
            catch { }
            try { response.Dispose(); }
            catch { }
        }
    }
}
