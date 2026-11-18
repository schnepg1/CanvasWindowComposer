namespace CanvasDesktop.Dictation;

/// <summary>An open Server-Sent Events stream from the local transcription server.</summary>
public interface IDictationStream : IAsyncDisposable
{
    IAsyncEnumerable<DictationStreamEvent> ReadEventsAsync(CancellationToken cancellationToken = default);
}

/// <summary>HTTP/SSE protocol client for a local headless speech-to-text transcription server.</summary>
public interface IDictationClient : IDisposable
{
    string BaseUrl { get; }

    Task<DictationHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default);

    Task<IDictationStream> OpenStreamAsync(CancellationToken cancellationToken = default);

    Task<DictationStartResponse> StartRecordingAsync(CancellationToken cancellationToken = default);

    Task<DictationStopResponse> StopRecordingAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}
