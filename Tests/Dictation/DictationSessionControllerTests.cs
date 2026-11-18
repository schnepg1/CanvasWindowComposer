using System.Threading.Channels;
using CanvasDesktop.Dictation;

namespace CanvasDesktop.Dictation.Tests;

public class DictationSessionControllerTests
{
    [Fact]
    public async Task StreamingLifecycleOpensStreamBeforeStartBuffersEarlyEventsFiltersForeignIdsAndFinalizesOnStop()
    {
        var client = new FakeDictationClient
        {
            HealthResponse = new DictationHealthResponse("ok", Ready: true, StreamingSupported: true, Recording: false),
            StartResponse = new DictationStartResponse("recording", RecordingId: "rec-1", Mode: "mic", Streaming: true),
            StopResponse = new DictationStopResponse("stopped", RecordingId: "rec-1", Text: "hello world!", Streaming: true)
        };

        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnStartRecording = async () =>
        {
            startEntered.TrySetResult();
            await releaseStart.Task;
        };

        var states = new List<DictationState>();
        var updates = new List<DictationTextUpdate>();
        var errors = new List<string>();
        string currentText = "Intro";

        using var controller = new DictationSessionController(
            client,
            () => new DictationEditorSnapshot(currentText, IsFocused: true, SelectionStart: 5, SelectionLength: 0),
            maxTextLength: 500);
        controller.StateChanged += s => states.Add(s);
        controller.TextUpdated += u => { currentText = u.Text; updates.Add(u); };
        controller.ErrorOccurred += e => errors.Add(e);

        var startTask = controller.ToggleAsync();
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Verify strict call ordering: health -> stream -> start
        Assert.Equal(["health", "stream", "start"], client.Calls);
        Assert.Equal(DictationState.Starting, controller.State);

        // Push early SSE events BEFORE POST /recording/start returns (one foreign, one matching)
        client.ActiveStream!.Push(new DictationStreamEvent(Type: "partial", RecordingId: "rec-other", FullText: "FOREIGN"));
        client.ActiveStream.Push(new DictationStreamEvent(Type: "partial", RecordingId: "rec-1", FullText: "hel"));
        await Task.Delay(40);
        Assert.Empty(updates); // Buffered while awaiting start response

        releaseStart.TrySetResult();
        await startTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DictationState.Recording, controller.State);
        Assert.Equal("rec-1", controller.ActiveRecordingId);
        Assert.Equal("Intro hel", currentText);

        // Live streaming partial + foreign event ignored
        client.ActiveStream.Push(new DictationStreamEvent(Type: "partial", RecordingId: "rec-other", FullText: "IGNORE"));
        client.ActiveStream.Push(new DictationStreamEvent(Type: "partial", RecordingId: "rec-1", FullText: "hello wor"));
        await WaitUntilAsync(() => currentText == "Intro hello wor");

        // Stop finalizes with stop response text and closes stream
        await controller.ToggleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal("Intro hello world!", currentText);
        Assert.Equal(currentText.Length, updates[^1].CaretIndex);
        Assert.True(client.ActiveStream.Disposed);
        Assert.Empty(errors);
        Assert.Equal([DictationState.Starting, DictationState.Recording, DictationState.Finalizing, DictationState.Idle], states);
    }

    [Fact]
    public async Task NonStreamingModeSkipsStreamEndpointAndInsertsOnStop()
    {
        var client = new FakeDictationClient
        {
            HealthResponse = new DictationHealthResponse("ok", Ready: true, StreamingSupported: false, Recording: false),
            StartResponse = new DictationStartResponse("recording", RecordingId: "rec-2", Mode: "mic", Streaming: false),
            StopResponse = new DictationStopResponse("stopped", RecordingId: "rec-2", Text: "batch result", Streaming: false)
        };

        string currentText = "Notes:";
        int lastCaret = 0;
        using var controller = new DictationSessionController(
            client,
            () => new DictationEditorSnapshot(currentText, IsFocused: false, SelectionStart: 0, SelectionLength: 0),
            maxTextLength: 500);
        controller.TextUpdated += u => { currentText = u.Text; lastCaret = u.CaretIndex; };

        await controller.StartAsync();
        Assert.Equal(["health", "start"], client.Calls);
        Assert.Equal(DictationState.Recording, controller.State);

        await controller.StopAsync();
        Assert.Equal(["health", "start", "stop"], client.Calls);
        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal("Notes: batch result", currentText);
        Assert.Equal(currentText.Length, lastCaret);
    }

    [Fact]
    public async Task FallsBackToNonStreamingWhenStartReturnsStreamingFalse()
    {
        var client = new FakeDictationClient
        {
            HealthResponse = new DictationHealthResponse("ok", Ready: true, StreamingSupported: true, Recording: false),
            StartResponse = new DictationStartResponse("recording", RecordingId: "rec-3", Mode: "mic", Streaming: false),
            StopResponse = new DictationStopResponse("stopped", RecordingId: "rec-3", Text: "fallback text", Streaming: false)
        };

        string currentText = "";
        var errors = new List<string>();
        using var controller = new DictationSessionController(
            client,
            () => new DictationEditorSnapshot(currentText, IsFocused: true, SelectionStart: 0, SelectionLength: 0),
            maxTextLength: 500);
        controller.TextUpdated += u => currentText = u.Text;
        controller.ErrorOccurred += errors.Add;

        await controller.StartAsync();
        Assert.Equal(DictationState.Recording, controller.State);
        Assert.True(client.ActiveStream!.Disposed);
        // Allow the background stream pump to observe stream closure and exit cleanly.
        await Task.Delay(50);
        Assert.Empty(errors);

        await controller.StopAsync();
        Assert.Equal("fallback text", currentText);
        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ConcurrentLiveEventDuringBufferedReplayPreservesStrictFifoOrder()
    {
        var client = new FakeDictationClient
        {
            HealthResponse = new DictationHealthResponse("ok", Ready: true, StreamingSupported: true),
            StartResponse = new DictationStartResponse("recording", RecordingId: "rec-fifo", Mode: "mic", Streaming: true),
            StopResponse = new DictationStopResponse("stopped", RecordingId: "rec-fifo", Text: "one two", Streaming: true)
        };

        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnStartRecording = async () =>
        {
            startEntered.TrySetResult();
            await releaseStart.Task;
        };

        var observedTexts = new List<string>();
        string currentText = "";
        using var controller = new DictationSessionController(
            client,
            () => new DictationEditorSnapshot(currentText, IsFocused: true, SelectionStart: 0, SelectionLength: 0),
            maxTextLength: 500);

        bool pushedLiveEventDuringReplay = false;
        controller.TextUpdated += u =>
        {
            currentText = u.Text;
            observedTexts.Add(u.Text);
            if (!pushedLiveEventDuringReplay && u.Text == "one")
            {
                pushedLiveEventDuringReplay = true;
                // Push a newer live SSE event while the buffered event replay callback is still executing.
                client.ActiveStream!.Push(new DictationStreamEvent(Type: "partial", RecordingId: "rec-fifo", FullText: "one two"));
                Thread.Sleep(40);
            }
        };

        var startTask = controller.StartAsync();
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Buffer an early event before POST /recording/start returns.
        client.ActiveStream!.Push(new DictationStreamEvent(Type: "partial", RecordingId: "rec-fifo", FullText: "one"));
        await Task.Delay(30);

        releaseStart.TrySetResult();
        await startTask.WaitAsync(TimeSpan.FromSeconds(2));

        // A null partial payload must not wipe the active partial span.
        client.ActiveStream.Push(new DictationStreamEvent(Type: "partial", RecordingId: "rec-fifo", FullText: null, Text: null));
        await Task.Delay(30);

        Assert.Equal(["one", "one two"], observedTexts);
        Assert.Equal("one two", currentText);
    }

    [Fact]
    public async Task DisposeDuringOpenStreamAndWithPendingAbortAlwaysDisposesOwnedClient()
    {
        // Case 1: Dispose while OpenStreamAsync is completing (before StartRecordingAsync is dispatched)
        var openEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client1 = new FakeDictationClient
        {
            HealthResponse = new DictationHealthResponse("ok", Ready: true, StreamingSupported: true),
            OnOpenStream = async () =>
            {
                openEntered.TrySetResult();
                await releaseOpen.Task;
            }
        };

        var controller1 = new DictationSessionController(client1, () => new("", true, 0, 0), 500, ownsClient: true);
        var startTask1 = controller1.StartAsync();
        await openEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        controller1.Dispose();
        releaseOpen.TrySetResult();
        await startTask1.WaitAsync(TimeSpan.FromSeconds(2));

        await WaitUntilAsync(() => client1.Disposed);
        Assert.True(client1.ActiveStream!.Disposed);
        Assert.DoesNotContain("start", client1.Calls);
        Assert.DoesNotContain("stop", client1.Calls);

        // Case 2: AbortInBackground leaves a pending stop task, then a second session opens and Dispose runs
        var firstAbortStopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstAbortStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stopCalls = 0;
        var client2 = new FakeDictationClient
        {
            HealthResponse = new DictationHealthResponse("ok", Ready: true, StreamingSupported: true),
            StartResponse = new DictationStartResponse("recording", RecordingId: "rec-a", Mode: "mic", Streaming: true),
            OnStopRecording = async _ =>
            {
                if (Interlocked.Increment(ref stopCalls) == 1)
                {
                    firstAbortStopEntered.TrySetResult();
                    await releaseFirstAbortStop.Task;
                }
            }
        };

        var controller2 = new DictationSessionController(client2, () => new("", true, 0, 0), 500, ownsClient: true);
        await controller2.StartAsync();
        controller2.AbortInBackground();
        await firstAbortStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Start a second session while the prior abort stop is still in flight, then dispose.
        await controller2.StartAsync();
        var secondStream = client2.ActiveStream!;
        controller2.Dispose();
        await Task.Delay(30);
        Assert.False(client2.Disposed);

        releaseFirstAbortStop.TrySetResult();
        await WaitUntilAsync(() => client2.Disposed);
        Assert.True(secondStream.Disposed);
    }

    [Fact]
    public async Task UnreachableAndNotReadyHealthEmitErrorsAndReturnToIdle()
    {
        var unreachableClient = new FakeDictationClient
        {
            HealthException = new DictationClientException(DictationErrorKind.ServerUnreachable, DictationDefaults.UnreachableMessage(null))
        };
        var errors = new List<string>();
        using (var c = new DictationSessionController(unreachableClient, () => new("", true, 0, 0), 500))
        {
            c.ErrorOccurred += errors.Add;
            await c.StartAsync();
            Assert.Equal(DictationState.Idle, c.State);
            Assert.Equal(["health"], unreachableClient.Calls);
            Assert.Single(errors);
            Assert.Contains("127.0.0.1:8765", errors[0]);
        }

        var loadingClient = new FakeDictationClient
        {
            HealthResponse = new DictationHealthResponse("loading", Ready: false)
        };
        errors.Clear();
        using (var c = new DictationSessionController(loadingClient, () => new("", true, 0, 0), 500))
        {
            c.ErrorOccurred += errors.Add;
            await c.StartAsync();
            Assert.Equal(DictationState.Idle, c.State);
            Assert.Single(errors);
            Assert.Equal(DictationDefaults.ModelLoadingMessage, errors[0]);
        }
    }

    [Fact]
    public async Task ServerSideCompletedAndErrorStreamEventsTransitionCleanlyToIdle()
    {
        var client = new FakeDictationClient
        {
            HealthResponse = new DictationHealthResponse("ok", Ready: true, StreamingSupported: true),
            StartResponse = new DictationStartResponse("recording", RecordingId: "rec-c", Streaming: true)
        };

        string currentText = "Hello";
        using (var controller = new DictationSessionController(client, () => new(currentText, true, 5, 0), 500))
        {
            controller.TextUpdated += u => currentText = u.Text;
            await controller.StartAsync();

            client.ActiveStream!.Push(new DictationStreamEvent(Type: "completed", RecordingId: "rec-c", Text: "done"));
            await WaitUntilAsync(() => controller.State == DictationState.Idle);
            Assert.Equal("Hello done", currentText);
            Assert.True(client.ActiveStream.Disposed);
        }

        var errorClient = new FakeDictationClient
        {
            HealthResponse = new DictationHealthResponse("ok", Ready: true, StreamingSupported: true),
            StartResponse = new DictationStartResponse("recording", RecordingId: "rec-e", Streaming: true)
        };

        string? receivedError = null;
        using (var controller = new DictationSessionController(errorClient, () => new("", true, 0, 0), 500))
        {
            controller.ErrorOccurred += e => receivedError = e;
            await controller.StartAsync();

            errorClient.ActiveStream!.Push(new DictationStreamEvent(Type: "error", RecordingId: "rec-e", Message: "Audio device lost"));
            await WaitUntilAsync(() => controller.State == DictationState.Idle);
            Assert.Equal("Audio device lost", receivedError);
            Assert.True(errorClient.ActiveStream.Disposed);
        }
    }

    [Fact]
    public async Task UnexpectedSseDisconnectEmitsNoticeAndStillAllowsManualStop()
    {
        var client = new FakeDictationClient
        {
            HealthResponse = new DictationHealthResponse("ok", Ready: true, StreamingSupported: true),
            StartResponse = new DictationStartResponse("recording", RecordingId: "rec-d", Streaming: true),
            StopResponse = new DictationStopResponse("stopped", RecordingId: "rec-d", Text: "final after drop", Streaming: true)
        };

        string? notice = null;
        string currentText = "";
        using var controller = new DictationSessionController(client, () => new(currentText, true, 0, 0), 500);
        controller.ErrorOccurred += e => notice = e;
        controller.TextUpdated += u => currentText = u.Text;

        await controller.StartAsync();
        client.ActiveStream!.CompleteUnexpectedly();

        await WaitUntilAsync(() => notice != null);
        Assert.Equal(DictationState.Recording, controller.State);

        await controller.StopAsync();
        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal("final after drop", currentText);
    }

    [Fact]
    public async Task DisposeWhileRecordingSendsDetachedStopAndDisposesOwnedClientAfterwards()
    {
        var stopCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeDictationClient
        {
            HealthResponse = new DictationHealthResponse("ok", Ready: true, StreamingSupported: true),
            StartResponse = new DictationStartResponse("recording", RecordingId: "rec-x", Streaming: true),
            StopResponse = new DictationStopResponse("stopped", RecordingId: "rec-x", Text: "ignored"),
            OnStopRecording = timeout =>
            {
                Assert.Equal(DictationDefaults.AbortStopTimeout, timeout);
                stopCalled.TrySetResult();
                return Task.CompletedTask;
            }
        };

        int uiCallbacksAfterDispose = 0;
        bool disposed = false;
        var controller = new DictationSessionController(client, () => new("", true, 0, 0), 500, ownsClient: true);
        controller.StateChanged += _ => { if (disposed) uiCallbacksAfterDispose++; };
        controller.TextUpdated += _ => { if (disposed) uiCallbacksAfterDispose++; };
        controller.ErrorOccurred += _ => { if (disposed) uiCallbacksAfterDispose++; };

        await controller.StartAsync();
        disposed = true;
        controller.Dispose();

        await stopCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => client.Disposed);
        Assert.Equal(0, uiCallbacksAfterDispose);
        Assert.True(client.ActiveStream!.Disposed);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition timed out.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeDictationStream : IDictationStream
    {
        private readonly Channel<DictationStreamEvent> _channel = Channel.CreateUnbounded<DictationStreamEvent>();
        public bool Disposed { get; private set; }

        public void Push(DictationStreamEvent ev) => _channel.Writer.TryWrite(ev);
        public void CompleteUnexpectedly() => _channel.Writer.TryComplete();

        public async IAsyncEnumerable<DictationStreamEvent> ReadEventsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken))
                yield return item;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeDictationClient : IDictationClient
    {
        public string BaseUrl => DictationDefaults.DefaultBaseUrl;
        public List<string> Calls { get; } = new();
        public DictationHealthResponse HealthResponse { get; set; } = new("ok", Ready: true);
        public Exception? HealthException { get; set; }
        public DictationStartResponse StartResponse { get; set; } = new("recording", RecordingId: "rec-1", Mode: "mic");
        public DictationStopResponse StopResponse { get; set; } = new("stopped", RecordingId: "rec-1", Text: "");
        public Func<Task>? OnOpenStream { get; set; }
        public Func<Task>? OnStartRecording { get; set; }
        public Func<TimeSpan?, Task>? OnStopRecording { get; set; }
        public FakeDictationStream? ActiveStream { get; private set; }
        public bool Disposed { get; private set; }

        public Task<DictationHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("health");
            if (HealthException != null) throw HealthException;
            return Task.FromResult(HealthResponse);
        }

        public async Task<IDictationStream> OpenStreamAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("stream");
            if (OnOpenStream != null) await OnOpenStream();
            ActiveStream = new FakeDictationStream();
            return ActiveStream;
        }

        public async Task<DictationStartResponse> StartRecordingAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("start");
            if (OnStartRecording != null) await OnStartRecording();
            return StartResponse;
        }

        public async Task<DictationStopResponse> StopRecordingAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Calls.Add("stop");
            if (OnStopRecording != null) await OnStopRecording(timeout);
            return StopResponse;
        }

        public void Dispose() => Disposed = true;
    }
}
