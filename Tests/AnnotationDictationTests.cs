using System.Threading.Channels;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using CanvasDesktop.Dictation;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class AnnotationDictationTests
{
    private static CanvasAnnotation Note(string text = "Hello world") => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        AnnotationKind.Text,
        new(100, 100),
        new(380, 160),
        Text: text);

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> predicate, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting for WPF dictation UI state.");
            dispatcher.Invoke(DispatcherPriority.Background, () => { });
            Thread.Sleep(10);
        }
        dispatcher.Invoke(DispatcherPriority.Background, () => { });
    }

    [Fact]
    public void MicButtonIdleAndUnreachableErrorShowNonBlockingHintAndStayEditable()
        => WorkspaceViewTests.OnSta(() =>
        {
            var client = new FakeDictationClient
            {
                HealthException = new DictationClientException(
                    DictationErrorKind.ServerUnreachable,
                    DictationDefaults.UnreachableMessage("http://127.0.0.1:8765"))
            };

            using var editor = new AnnotationTextEditor(Note("Keep text"), dictationClient: client);
            editor.Show();
            System.Windows.Forms.Application.DoEvents();

            Assert.Equal(DictationState.Idle, editor.DictationState);
            Assert.True(editor.DictationButton.IsEnabled);
            Assert.False(editor.DictationButton.Focusable);
            Assert.Equal("Start dictation", AutomationProperties.GetName(editor.DictationButton));

            editor.DictationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(editor.Dispatcher, () =>
                editor.DictationState == DictationState.Idle
                && editor.HintTextBlock.Text.Contains("127.0.0.1:8765"));

            Assert.Equal("Local transcription server is not running on 127.0.0.1:8765", editor.HintTextBlock.Text);
            Assert.Equal(Brushes.LightCoral, editor.HintTextBlock.Foreground);
            Assert.Equal("Keep text", editor.Editor.Text);
            Assert.True(editor.DictationButton.IsEnabled);
        });

    [Fact]
    public void ModelLoadingShowsNonBlockingErrorWithoutModifyingText()
        => WorkspaceViewTests.OnSta(() =>
        {
            var client = new FakeDictationClient
            {
                HealthResponse = new DictationHealthResponse("loading", Ready: false)
            };

            using var editor = new AnnotationTextEditor(Note("Draft"), dictationClient: client);
            editor.Show();

            editor.DictationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(editor.Dispatcher, () =>
                editor.HintTextBlock.Text == DictationDefaults.ModelLoadingMessage);

            Assert.Equal(DictationState.Idle, editor.DictationState);
            Assert.Equal("Draft", editor.Editor.Text);
            Assert.Equal(Brushes.LightCoral, editor.HintTextBlock.Foreground);
        });

    [Fact]
    public void StreamingModeUpdatesLiveSpanInPlaceShowsButtonStatesAndPositionsCaretAtEndOnStop()
        => WorkspaceViewTests.OnSta(() =>
        {
            var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new FakeDictationClient
            {
                HealthResponse = new DictationHealthResponse("ok", Ready: true, StreamingSupported: true),
                StartResponse = new DictationStartResponse("recording", RecordingId: "rec-ui-1", Mode: "mic", Streaming: true),
                StopResponse = new DictationStopResponse("stopped", RecordingId: "rec-ui-1", Text: "dear friend", Streaming: true),
                OnStop = () => stopGate.Task
            };

            using var editor = new AnnotationTextEditor(Note("Hello world"), dictationClient: client);
            editor.Show();
            editor.Editor.CaretIndex = 5;
            editor.Editor.SelectionStart = 5;
            editor.Editor.SelectionLength = 0;

            editor.DictationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(editor.Dispatcher, () => editor.DictationState == DictationState.Recording);

            Assert.Equal("Stop dictation", AutomationProperties.GetName(editor.DictationButton));
            Assert.True(editor.DictationButton.IsEnabled);
            var bg = Assert.IsType<SolidColorBrush>(editor.DictationButton.Background);
            Assert.Equal(Color.FromRgb(0xDC, 0x26, 0x26), bg.Color);

            client.ActiveStream!.Push(new DictationStreamEvent(Type: "partial", RecordingId: "rec-ui-1", FullText: "dear"));
            PumpUntil(editor.Dispatcher, () => editor.Editor.Text == "Hello dear world");
            Assert.Equal(10, editor.Editor.CaretIndex);

            client.ActiveStream.Push(new DictationStreamEvent(Type: "partial", RecordingId: "rec-ui-1", FullText: "dear fri"));
            PumpUntil(editor.Dispatcher, () => editor.Editor.Text == "Hello dear fri world");

            // Click Stop -> enters Finalizing state until stop completes
            editor.DictationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(editor.Dispatcher, () => editor.DictationState == DictationState.Finalizing);
            Assert.False(editor.DictationButton.IsEnabled);
            Assert.Equal("Finalizing transcription", AutomationProperties.GetName(editor.DictationButton));

            stopGate.TrySetResult();
            PumpUntil(editor.Dispatcher, () => editor.DictationState == DictationState.Idle);

            Assert.Equal("Hello dear friend world", editor.Editor.Text);
            Assert.Equal(17, editor.Editor.CaretIndex);
            Assert.Equal("Start dictation", AutomationProperties.GetName(editor.DictationButton));
            Assert.True(editor.DictationButton.IsEnabled);
            Assert.True(client.ActiveStream.Disposed);
        });

    [Fact]
    public void NonStreamingModeAppendsWhenEditorUnfocusedAndInsertsOnStop()
        => WorkspaceViewTests.OnSta(() =>
        {
            var client = new FakeDictationClient
            {
                HealthResponse = new DictationHealthResponse("ok", Ready: true, StreamingSupported: false),
                StartResponse = new DictationStartResponse("recording", RecordingId: "rec-ns", Mode: "mic", Streaming: false),
                StopResponse = new DictationStopResponse("stopped", RecordingId: "rec-ns", Text: "appended sentence", Streaming: false)
            };

            // Intentionally keep editor unfocused (not focused and CaretIndex at 0)
            using var editor = new AnnotationTextEditor(Note("Existing line"), dictationClient: client);
            editor.Editor.SelectionStart = 0;
            editor.Editor.SelectionLength = 4;
            FocusManager.SetFocusedElement(editor, null);

            editor.DictationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(editor.Dispatcher, () => editor.DictationState == DictationState.Recording);

            Assert.Equal(["health", "start"], client.Calls);

            editor.DictationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(editor.Dispatcher, () => editor.DictationState == DictationState.Idle);

            Assert.Equal("Existing line appended sentence", editor.Editor.Text);
            Assert.Equal(editor.Editor.Text.Length, editor.Editor.CaretIndex);
        });

    [Fact]
    public void EmptyTranscriptPreservesSelectionAndDisposeAbortsActiveRecordingInBackground()
        => WorkspaceViewTests.OnSta(() =>
        {
            var emptyClient = new FakeDictationClient
            {
                HealthResponse = new DictationHealthResponse("ok", Ready: true, StreamingSupported: false),
                StartResponse = new DictationStartResponse("recording", RecordingId: "rec-empty", Mode: "mic", Streaming: false),
                StopResponse = new DictationStopResponse("stopped", RecordingId: "rec-empty", Text: "   ", Streaming: false)
            };

            using (var editor = new AnnotationTextEditor(Note("Select me"), dictationClient: emptyClient))
            {
                editor.Show();
                editor.Editor.Select(0, 6);
                editor.DictationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(editor.Dispatcher, () => editor.DictationState == DictationState.Recording);

                editor.DictationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(editor.Dispatcher, () => editor.DictationState == DictationState.Idle);

                Assert.Equal("Select me", editor.Editor.Text);
            }

            var abortStopSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var abortClient = new FakeDictationClient
            {
                HealthResponse = new DictationHealthResponse("ok", Ready: true, StreamingSupported: true),
                StartResponse = new DictationStartResponse("recording", RecordingId: "rec-abort", Mode: "mic", Streaming: true),
                OnStop = () => { abortStopSeen.TrySetResult(); return Task.CompletedTask; }
            };

            var closingEditor = new AnnotationTextEditor(Note("Closing"), dictationClient: abortClient);
            closingEditor.Show();
            closingEditor.DictationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(closingEditor.Dispatcher, () => closingEditor.DictationState == DictationState.Recording);

            closingEditor.Dispose();
            Assert.True(abortStopSeen.Task.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(abortClient.ActiveStream!.Disposed);
        });

    private sealed class FakeDictationStream : IDictationStream
    {
        private readonly Channel<DictationStreamEvent> _channel = Channel.CreateUnbounded<DictationStreamEvent>();
        public bool Disposed { get; private set; }

        public void Push(DictationStreamEvent ev) => _channel.Writer.TryWrite(ev);

        public async IAsyncEnumerable<DictationStreamEvent> ReadEventsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var ev in _channel.Reader.ReadAllAsync(cancellationToken))
                yield return ev;
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
        public Func<Task>? OnStop { get; set; }
        public FakeDictationStream? ActiveStream { get; private set; }

        public Task<DictationHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("health");
            if (HealthException != null) throw HealthException;
            return Task.FromResult(HealthResponse);
        }

        public Task<IDictationStream> OpenStreamAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("stream");
            ActiveStream = new FakeDictationStream();
            return Task.FromResult<IDictationStream>(ActiveStream);
        }

        public Task<DictationStartResponse> StartRecordingAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("start");
            return Task.FromResult(StartResponse);
        }

        public async Task<DictationStopResponse> StopRecordingAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Calls.Add("stop");
            if (OnStop != null) await OnStop();
            return StopResponse;
        }

        public void Dispose() { }
    }
}
