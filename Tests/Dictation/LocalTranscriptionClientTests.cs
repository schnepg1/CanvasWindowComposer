using System.Net;
using System.Text;
using System.Text.Json;
using CanvasDesktop.Dictation;

namespace CanvasDesktop.Dictation.Tests;

public class LocalTranscriptionClientTests
{
    [Fact]
    public async Task StartRecordingSendsModeMicAndStrictlyOmitsStreamingField()
    {
        string? capturedBody = null;
        string? capturedUri = null;
        HttpMethod? capturedMethod = null;

        var handler = new StubHttpMessageHandler(async (req, _) =>
        {
            capturedMethod = req.Method;
            capturedUri = req.RequestUri?.ToString();
            capturedBody = req.Content != null ? await req.Content.ReadAsStringAsync() : null;
            return JsonResponse(HttpStatusCode.OK, """{"status":"recording","recording_id":"rec-42","mode":"mic","streaming":true,"model":"base"}""");
        });

        using var client = new LocalTranscriptionClient(handler, "http://127.0.0.1:8765/");
        var response = await client.StartRecordingAsync();

        Assert.Equal(HttpMethod.Post, capturedMethod);
        Assert.Equal("http://127.0.0.1:8765/recording/start", capturedUri);
        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("mic", doc.RootElement.GetProperty("mode").GetString());
        Assert.False(doc.RootElement.TryGetProperty("streaming", out _));
        Assert.Equal("rec-42", response.RecordingId);
        Assert.True(response.Streaming);
    }

    [Fact]
    public async Task HealthAndStopParseResponsesCorrectly()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/health")
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"status":"ok","ready":true,"model":"small","server_mode":"local","streaming_supported":true,"recording":false}"""));
            if (req.RequestUri!.AbsolutePath == "/recording/stop")
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"status":"stopped","recording_id":"rec-1","text":"hello world","segments":[{"text":"hello world"}],"duration":1.25,"streaming":true}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var client = new LocalTranscriptionClient(handler, "http://127.0.0.1:8765");
        var health = await client.GetHealthAsync();
        Assert.True(health.Ready);
        Assert.True(health.StreamingSupported);
        Assert.False(health.Recording);

        var stop = await client.StopRecordingAsync();
        Assert.Equal("hello world", stop.Text);
        Assert.Equal("rec-1", stop.RecordingId);
        Assert.Equal(1.25, stop.Duration);
    }

    [Fact]
    public async Task MapsUnreachable503409AndServerErrorProperly()
    {
        var unreachableHandler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Connection refused"));
        using (var client = new LocalTranscriptionClient(unreachableHandler, "http://127.0.0.1:8765"))
        {
            var ex = await Assert.ThrowsAsync<DictationClientException>(() => client.GetHealthAsync());
            Assert.Equal(DictationErrorKind.ServerUnreachable, ex.Kind);
            Assert.Equal("Local transcription server is not running on 127.0.0.1:8765", ex.Message);
        }

        var loadingHandler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.ServiceUnavailable, """{"detail":"still loading"}""")));
        using (var client = new LocalTranscriptionClient(loadingHandler))
        {
            var ex = await Assert.ThrowsAsync<DictationClientException>(() => client.StartRecordingAsync());
            Assert.Equal(DictationErrorKind.ModelLoading, ex.Kind);
            Assert.Equal(DictationDefaults.ModelLoadingMessage, ex.Message);
            Assert.Equal(503, ex.StatusCode);
        }

        var conflictHandler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.Conflict, """{"detail":"busy"}""")));
        using (var client = new LocalTranscriptionClient(conflictHandler))
        {
            var ex = await Assert.ThrowsAsync<DictationClientException>(() => client.StartRecordingAsync());
            Assert.Equal(DictationErrorKind.AlreadyRecording, ex.Kind);
            Assert.Equal(DictationDefaults.AlreadyRecordingMessage, ex.Message);
            Assert.Equal(409, ex.StatusCode);
        }

        var errorHandler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.InternalServerError, """{"detail":"Microphone device failed"}""")));
        using (var client = new LocalTranscriptionClient(errorHandler))
        {
            var ex = await Assert.ThrowsAsync<DictationClientException>(() => client.StopRecordingAsync());
            Assert.Equal(DictationErrorKind.ServerError, ex.Kind);
            Assert.Equal("Microphone device failed", ex.Message);
            Assert.Equal(500, ex.StatusCode);
        }
    }

    [Fact]
    public async Task OpenStreamSendsSseHeadersAndSkipsMalformedJsonLinesGracefully()
    {
        string? acceptHeader = null;
        bool? noCache = null;

        string ssePayload =
            ": keepalive\n\n" +
            "data: {not valid json}\n\n" +
            "data: {\"type\":\"partial\",\"recording_id\":\"r1\",\"full_text\":\"hello\"}\n\n" +
            "data: {\"type\":\"final\",\"recording_id\":\"r1\",\"full_text\":\"hello world\"}\n\n";

        var handler = new StubHttpMessageHandler((req, _) =>
        {
            acceptHeader = req.Headers.Accept.ToString();
            noCache = req.Headers.CacheControl?.NoCache;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(ssePayload)))
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        });

        using var client = new LocalTranscriptionClient(handler);
        await using var stream = await client.OpenStreamAsync();
        Assert.Contains("text/event-stream", acceptHeader);
        Assert.True(noCache);

        var events = new List<DictationStreamEvent>();
        await foreach (var ev in stream.ReadEventsAsync())
            events.Add(ev);

        Assert.Equal(2, events.Count);
        Assert.Equal("partial", events[0].Type);
        Assert.Equal("hello", events[0].FullText);
        Assert.Equal("final", events[1].Type);
        Assert.Equal("hello world", events[1].FullText);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
