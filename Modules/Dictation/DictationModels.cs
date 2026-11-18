using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasDesktop.Dictation;

public enum DictationState
{
    Idle,
    Starting,
    Recording,
    Finalizing
}

public enum DictationErrorKind
{
    ServerUnreachable,
    ModelLoading,
    AlreadyRecording,
    ServerError,
    StreamError,
    InvalidResponse
}

public sealed record DictationHealthResponse(
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("ready")] bool Ready = false,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("server_mode")] string? ServerMode = null,
    [property: JsonPropertyName("streaming_supported")] bool StreamingSupported = false,
    [property: JsonPropertyName("recording")] bool Recording = false);

/// <summary>Request payload for POST /recording/start. Intentionally omits any streaming override.</summary>
public sealed record DictationStartRequest(
    [property: JsonPropertyName("mode")] string Mode = "mic");

public sealed record DictationStartResponse(
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("recording_id")] string? RecordingId = null,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("streaming")] bool Streaming = false,
    [property: JsonPropertyName("model")] string? Model = null);

public sealed record DictationStopResponse(
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("recording_id")] string? RecordingId = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("segments")] IReadOnlyList<JsonElement>? Segments = null,
    [property: JsonPropertyName("duration")] double? Duration = null,
    [property: JsonPropertyName("streaming")] bool Streaming = false);

public sealed record DictationStreamEvent(
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("recording_id")] string? RecordingId = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("full_text")] string? FullText = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("detail")] string? Detail = null)
{
    [JsonIgnore]
    public string? EffectiveErrorMessage =>
        !string.IsNullOrWhiteSpace(Message) ? Message
        : !string.IsNullOrWhiteSpace(Error) ? Error
        : !string.IsNullOrWhiteSpace(Detail) ? Detail
        : null;
}

public sealed class DictationClientException : Exception
{
    public DictationErrorKind Kind { get; }
    public int? StatusCode { get; }

    public DictationClientException(
        DictationErrorKind kind,
        string message,
        int? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }
}
