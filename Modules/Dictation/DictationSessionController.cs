namespace CanvasDesktop.Dictation;

/// <summary>Snapshot of the host text editor state used when anchoring a dictation session.</summary>
public readonly record struct DictationEditorSnapshot(
    string Text,
    bool IsFocused,
    int SelectionStart,
    int SelectionLength);

/// <summary>
/// Coordinates a dictation session lifecycle against a local transcription server with an explicit
/// state machine (Idle -> Starting -> Recording -> Finalizing -> Idle), strict FIFO SSE buffering/replay,
/// and non-blocking abort cleanup.
/// </summary>
public sealed class DictationSessionController : IDisposable
{
    private readonly object _gate = new();
    private readonly IDictationClient _client;
    private readonly Func<DictationEditorSnapshot> _captureSnapshot;
    private readonly int _maxTextLength;
    private readonly bool _ownsClient;
    private readonly DictationTextSpan _span = new();

    private DictationState _state = DictationState.Idle;
    private long _sessionGeneration;
    private string? _activeRecordingId;
    private bool _startRequestDispatched;
    private bool _bufferingStreamEvents;
    private bool _suppressStreamUpdates;
    private Queue<DictationStreamEvent>? _bufferedEvents;
    private IDictationStream? _activeStream;
    private CancellationTokenSource? _sessionCts;
    private string _trackedText = string.Empty;
    private Task? _pendingAbortTask;
    private bool _disposed;

    public DictationState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public string? ActiveRecordingId
    {
        get
        {
            lock (_gate)
                return _activeRecordingId;
        }
    }

    public event Action<DictationState>? StateChanged;
    public event Action<DictationTextUpdate>? TextUpdated;
    public event Action<string>? ErrorOccurred;

    public DictationSessionController(
        IDictationClient client,
        Func<DictationEditorSnapshot> captureSnapshot,
        int maxTextLength,
        bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _captureSnapshot = captureSnapshot ?? throw new ArgumentNullException(nameof(captureSnapshot));
        _maxTextLength = Math.Max(0, maxTextLength);
        _ownsClient = ownsClient;
    }

    public Task ToggleAsync(CancellationToken cancellationToken = default)
    {
        DictationState current;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            current = _state;
        }

        return current switch
        {
            DictationState.Idle => StartAsync(cancellationToken),
            DictationState.Recording => StopAsync(cancellationToken),
            _ => Task.CompletedTask
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        long gen;
        CancellationToken sessionToken;
        lock (_gate)
        {
            if (_disposed || _state != DictationState.Idle)
                return;

            gen = ++_sessionGeneration;
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sessionToken = _sessionCts.Token;
            _activeRecordingId = null;
            _startRequestDispatched = false;
            _bufferingStreamEvents = false;
            _suppressStreamUpdates = false;
            _bufferedEvents = null;
            _state = DictationState.Starting;
        }

        EmitState(DictationState.Starting);

        // 1. Verify server health and readiness before touching the editor anchor or opening streams.
        DictationHealthResponse health;
        try
        {
            health = await _client.GetHealthAsync(sessionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await ResetSessionAsync(gen).ConfigureAwait(false);
            return;
        }
        catch (ObjectDisposedException) when (IsDisposed())
        {
            return;
        }
        catch (DictationClientException ex)
        {
            await ResetSessionAsync(gen, ex.Message).ConfigureAwait(false);
            return;
        }

        if (!IsGenerationCurrent(gen))
            return;

        if (!health.Ready)
        {
            await ResetSessionAsync(gen, DictationDefaults.ModelLoadingMessage).ConfigureAwait(false);
            return;
        }

        if (health.Recording)
        {
            await ResetSessionAsync(gen, DictationDefaults.AlreadyRecordingMessage).ConfigureAwait(false);
            return;
        }

        // 2. Capture editor anchor right after health succeeds.
        DictationEditorSnapshot snapshot = _captureSnapshot();
        lock (_gate)
        {
            if (_disposed || _sessionGeneration != gen)
                return;

            _trackedText = snapshot.Text ?? string.Empty;
            _span.CaptureAnchor(_trackedText, snapshot.IsFocused, snapshot.SelectionStart, snapshot.SelectionLength);
        }

        // 3. When streaming is supported, open SSE stream BEFORE POST /recording/start so early events are never missed.
        IDictationStream? openedStream = null;
        if (health.StreamingSupported)
        {
            try
            {
                openedStream = await _client.OpenStreamAsync(sessionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ResetSessionAsync(gen).ConfigureAwait(false);
                return;
            }
            catch (ObjectDisposedException) when (IsDisposed())
            {
                return;
            }
            catch (DictationClientException ex)
            {
                await ResetSessionAsync(gen, ex.Message).ConfigureAwait(false);
                return;
            }

            bool attachStream;
            lock (_gate)
            {
                attachStream = !_disposed && _sessionGeneration == gen;
                if (attachStream)
                {
                    _activeStream = openedStream;
                    _bufferingStreamEvents = true;
                    _bufferedEvents = new Queue<DictationStreamEvent>();
                }
            }

            if (!attachStream)
            {
                await DisposeStreamQuietlyAsync(openedStream).ConfigureAwait(false);
                return;
            }

            _ = Task.Run(() => PumpStreamAsync(gen, openedStream, sessionToken));
        }

        // 4. Start recording on the server.
        lock (_gate)
        {
            if (_disposed || _sessionGeneration != gen)
                return;
            _startRequestDispatched = true;
        }

        DictationStartResponse startResponse;
        try
        {
            startResponse = await _client.StartRecordingAsync(sessionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await ResetSessionAsync(gen).ConfigureAwait(false);
            return;
        }
        catch (ObjectDisposedException) when (IsDisposed())
        {
            return;
        }
        catch (DictationClientException ex)
        {
            await ResetSessionAsync(gen, ex.Message).ConfigureAwait(false);
            return;
        }

        bool keepStreaming = false;
        IDictationStream? streamToClose = null;
        bool abortedInFlight = false;

        lock (_gate)
        {
            if (_disposed || _sessionGeneration != gen)
            {
                abortedInFlight = true;
            }
            else
            {
                _activeRecordingId = startResponse.RecordingId;
                keepStreaming = startResponse.Streaming && _activeStream != null;
                if (!keepStreaming)
                {
                    streamToClose = _activeStream;
                    _activeStream = null;
                    _bufferingStreamEvents = false;
                    _bufferedEvents = null;
                }

                _state = DictationState.Recording;
            }
        }

        if (abortedInFlight)
        {
            await DisposeStreamQuietlyAsync(openedStream).ConfigureAwait(false);
            // The server accepted start right as we aborted; send a short best-effort stop.
            _ = SendBestEffortAbortStopAsync();
            return;
        }

        if (streamToClose != null)
            await DisposeStreamQuietlyAsync(streamToClose).ConfigureAwait(false);

        EmitState(DictationState.Recording);

        if (keepStreaming)
            await DrainBufferedEventsAsync(gen).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        long gen;
        IDictationStream? streamToClose;
        CancellationTokenSource? oldCts;
        CancellationToken stopToken;

        lock (_gate)
        {
            if (_disposed || _state != DictationState.Recording || _suppressStreamUpdates)
                return;

            gen = _sessionGeneration;
            _suppressStreamUpdates = true;
            _bufferingStreamEvents = false;
            _bufferedEvents = null;
            _state = DictationState.Finalizing;
            streamToClose = _activeStream;
            _activeStream = null;
            oldCts = _sessionCts;
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stopToken = _sessionCts.Token;
        }

        EmitState(DictationState.Finalizing);
        CancelAndDisposeCts(oldCts);

        try
        {
            var response = await _client.StopRecordingAsync(DictationDefaults.StopTimeout, stopToken).ConfigureAwait(false);

            DictationTextUpdate? finalUpdate = null;

            lock (_gate)
            {
                if (!_disposed && _sessionGeneration == gen)
                {
                    bool hadApplied = _span.HasAppliedTranscript;
                    var update = _span.Apply(_trackedText, response.Text, _maxTextLength);
                    if (update.Changed)
                        _trackedText = update.Text;
                    if (update.Changed || hadApplied)
                        finalUpdate = update;
                }
            }

            if (finalUpdate.HasValue)
                EmitTextUpdate(finalUpdate.Value);

            CancellationTokenSource? stopCts = null;
            bool transitionedToIdle = false;
            lock (_gate)
            {
                if (!_disposed && _sessionGeneration == gen)
                {
                    (_, stopCts) = ClearSessionLocked(suppressUpdates: false);
                    _state = DictationState.Idle;
                    transitionedToIdle = true;
                }
            }

            CancelAndDisposeCts(stopCts);

            if (transitionedToIdle)
                EmitState(DictationState.Idle);
        }
        catch (OperationCanceledException)
        {
            await ResetSessionAsync(gen).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (IsDisposed())
        {
            return;
        }
        catch (DictationClientException ex)
        {
            await ResetSessionAsync(gen, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            await DisposeStreamQuietlyAsync(streamToClose).ConfigureAwait(false);
        }
    }

    public void NotifyExternalTextEdit(int changeOffset, int removedLength, int addedLength, string newText)
    {
        lock (_gate)
        {
            _trackedText = newText ?? string.Empty;
            if (_state is DictationState.Starting or DictationState.Recording or DictationState.Finalizing)
            {
                _span.AdjustForExternalEdit(changeOffset, removedLength, addedLength, _trackedText.Length);
            }
        }
    }

    public async Task AbortAsync()
    {
        var abort = BeginAbort(disposing: false);
        CancelAndDisposeCts(abort.CtsToCancel);
        await DisposeStreamQuietlyAsync(abort.StreamToClose).ConfigureAwait(false);

        if (abort.StateChangedToIdle)
            EmitState(DictationState.Idle);

        if (abort.NeedsServerStop)
            await SendBestEffortAbortStopAsync().ConfigureAwait(false);
    }

    /// <summary>Immediately suppresses UI updates and cancels local resources without blocking the calling thread.</summary>
    public void AbortInBackground()
    {
        AbortWork abort;
        lock (_gate)
        {
            if (_disposed)
                return;

            abort = BeginAbortLocked(disposing: false);
            if (abort.StreamToClose != null || abort.NeedsServerStop)
            {
                var stream = abort.StreamToClose;
                bool needsStop = abort.NeedsServerStop;
                _pendingAbortTask = Task.Run(async () =>
                {
                    await DisposeStreamQuietlyAsync(stream).ConfigureAwait(false);
                    if (needsStop)
                        await SendBestEffortAbortStopAsync().ConfigureAwait(false);
                });
            }
        }

        CancelAndDisposeCts(abort.CtsToCancel);

        if (abort.StateChangedToIdle)
            EmitState(DictationState.Idle);
    }

    public void Dispose()
    {
        AbortWork abort;
        Task? priorAbortTask;

        lock (_gate)
        {
            if (_disposed)
                return;

            abort = BeginAbortLocked(disposing: true);
            priorAbortTask = _pendingAbortTask;
        }

        CancelAndDisposeCts(abort.CtsToCancel);

        bool hasAsyncCleanup = abort.NeedsServerStop
            || abort.StreamToClose != null
            || (priorAbortTask != null && !priorAbortTask.IsCompleted);

        if (hasAsyncCleanup)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await DisposeStreamQuietlyAsync(abort.StreamToClose).ConfigureAwait(false);
                    if (abort.NeedsServerStop)
                        await SendBestEffortAbortStopAsync().ConfigureAwait(false);
                    if (priorAbortTask != null && !priorAbortTask.IsCompleted)
                    {
                        try { await priorAbortTask.ConfigureAwait(false); }
                        catch { }
                    }
                }
                finally
                {
                    if (_ownsClient)
                        DisposeClientQuietly();
                }
            });
        }
        else if (_ownsClient)
        {
            DisposeClientQuietly();
        }
    }

    private async Task DrainBufferedEventsAsync(long gen)
    {
        // Keep PumpStreamAsync enqueuing under _gate until the queue is empty, guaranteeing strict FIFO replay.
        while (true)
        {
            DictationStreamEvent nextEvent;
            lock (_gate)
            {
                if (_disposed || _sessionGeneration != gen || _suppressStreamUpdates || _state != DictationState.Recording)
                {
                    _bufferingStreamEvents = false;
                    _bufferedEvents = null;
                    return;
                }

                if (_bufferedEvents == null || _bufferedEvents.Count == 0)
                {
                    _bufferingStreamEvents = false;
                    _bufferedEvents = null;
                    return;
                }

                nextEvent = _bufferedEvents.Dequeue();
            }

            await ProcessStreamEventAsync(gen, nextEvent).ConfigureAwait(false);
        }
    }

    private async Task PumpStreamAsync(long gen, IDictationStream stream, CancellationToken token)
    {
        bool disconnectedUnexpectedly = false;
        try
        {
            await foreach (var ev in stream.ReadEventsAsync(token).ConfigureAwait(false))
            {
                bool buffered = false;
                lock (_gate)
                {
                    if (_disposed || _sessionGeneration != gen || _suppressStreamUpdates)
                        return;

                    if (_bufferingStreamEvents)
                    {
                        _bufferedEvents?.Enqueue(ev);
                        buffered = true;
                    }
                }

                if (!buffered)
                    await ProcessStreamEventAsync(gen, ev).ConfigureAwait(false);
            }

            disconnectedUnexpectedly = true;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            disconnectedUnexpectedly = true;
        }

        if (!disconnectedUnexpectedly)
            return;

        bool emitNotice = false;
        IDictationStream? streamToClose = null;
        lock (_gate)
        {
            if (_disposed || _sessionGeneration != gen || _suppressStreamUpdates)
                return;

            // Only emit a disconnect notice if this stream was still the active stream (not closed intentionally on fallback).
            if (ReferenceEquals(_activeStream, stream))
            {
                streamToClose = _activeStream;
                _activeStream = null;
                if (_state == DictationState.Recording)
                    emitNotice = true;
            }
        }

        await DisposeStreamQuietlyAsync(streamToClose).ConfigureAwait(false);

        if (emitNotice)
            EmitError(DictationDefaults.StreamDisconnectedMessage);
    }

    private async Task ProcessStreamEventAsync(long gen, DictationStreamEvent ev)
    {
        string? eventType = ev.Type?.Trim();
        if (string.IsNullOrEmpty(eventType))
            return;

        if (eventType.Equals("partial", StringComparison.OrdinalIgnoreCase)
            || eventType.Equals("final", StringComparison.OrdinalIgnoreCase))
        {
            DictationTextUpdate? updateToEmit = null;
            lock (_gate)
            {
                if (_disposed || _sessionGeneration != gen || _suppressStreamUpdates || _state != DictationState.Recording)
                    return;

                if (!MatchesRecordingId(_activeRecordingId, ev.RecordingId))
                    return;

                string? transcript = ev.FullText ?? ev.Text;
                if (transcript is null)
                    return;

                var update = _span.Apply(_trackedText, transcript, _maxTextLength);
                if (update.Changed)
                {
                    _trackedText = update.Text;
                    updateToEmit = update;
                }
            }

            if (updateToEmit.HasValue)
                EmitTextUpdate(updateToEmit.Value);

            return;
        }

        if (eventType.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            DictationTextUpdate? finalUpdate = null;
            IDictationStream? streamToClose;
            CancellationTokenSource? ctsToCancel;
            bool transitionedToIdle = false;

            lock (_gate)
            {
                if (_disposed || _sessionGeneration != gen || _suppressStreamUpdates || _state != DictationState.Recording)
                    return;

                if (!MatchesRecordingId(_activeRecordingId, ev.RecordingId))
                    return;

                string? finalTranscript = ev.Text ?? ev.FullText;
                if (finalTranscript != null)
                {
                    bool hadApplied = _span.HasAppliedTranscript;
                    var update = _span.Apply(_trackedText, finalTranscript, _maxTextLength);
                    if (update.Changed)
                        _trackedText = update.Text;
                    if (update.Changed || hadApplied)
                        finalUpdate = update;
                }

                (streamToClose, ctsToCancel) = ClearSessionLocked(suppressUpdates: true);
            }

            CancelAndDisposeCts(ctsToCancel);
            await DisposeStreamQuietlyAsync(streamToClose).ConfigureAwait(false);

            if (finalUpdate.HasValue)
                EmitTextUpdate(finalUpdate.Value);

            lock (_gate)
            {
                if (!_disposed && _sessionGeneration == gen && _state != DictationState.Idle)
                {
                    _suppressStreamUpdates = false;
                    _state = DictationState.Idle;
                    transitionedToIdle = true;
                }
            }

            if (transitionedToIdle)
                EmitState(DictationState.Idle);

            return;
        }

        if (eventType.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            IDictationStream? streamToClose;
            CancellationTokenSource? ctsToCancel;
            bool transitionedToIdle = false;

            lock (_gate)
            {
                if (_disposed || _sessionGeneration != gen || _suppressStreamUpdates)
                    return;

                if (!MatchesRecordingId(_activeRecordingId, ev.RecordingId))
                    return;

                (streamToClose, ctsToCancel) = ClearSessionLocked(suppressUpdates: true);
            }

            CancelAndDisposeCts(ctsToCancel);
            await DisposeStreamQuietlyAsync(streamToClose).ConfigureAwait(false);

            EmitError(ev.EffectiveErrorMessage ?? "Transcription failed.");

            lock (_gate)
            {
                if (!_disposed && _sessionGeneration == gen && _state != DictationState.Idle)
                {
                    _suppressStreamUpdates = false;
                    _state = DictationState.Idle;
                    transitionedToIdle = true;
                }
            }

            if (transitionedToIdle)
                EmitState(DictationState.Idle);
        }
    }

    private async Task ResetSessionAsync(long gen, string? errorMessage = null)
    {
        IDictationStream? streamToClose;
        CancellationTokenSource? ctsToCancel;
        bool transitionedToIdle = false;

        lock (_gate)
        {
            if (_disposed || _sessionGeneration != gen)
                return;

            (streamToClose, ctsToCancel) = ClearSessionLocked(suppressUpdates: false);
            if (_state != DictationState.Idle)
            {
                _state = DictationState.Idle;
                transitionedToIdle = true;
            }
        }

        CancelAndDisposeCts(ctsToCancel);
        await DisposeStreamQuietlyAsync(streamToClose).ConfigureAwait(false);

        if (transitionedToIdle)
            EmitState(DictationState.Idle);

        if (!string.IsNullOrWhiteSpace(errorMessage))
            EmitError(errorMessage);
    }

    private (IDictationStream? Stream, CancellationTokenSource? Cts) ClearSessionLocked(bool suppressUpdates)
    {
        _suppressStreamUpdates = suppressUpdates;
        var stream = _activeStream;
        _activeStream = null;
        var cts = _sessionCts;
        _sessionCts = null;
        _activeRecordingId = null;
        _startRequestDispatched = false;
        _bufferingStreamEvents = false;
        _bufferedEvents = null;
        _span.Reset();
        return (stream, cts);
    }

    private AbortWork BeginAbort(bool disposing)
    {
        lock (_gate)
            return BeginAbortLocked(disposing);
    }

    private AbortWork BeginAbortLocked(bool disposing)
    {
        if (disposing)
            _disposed = true;

        bool needsServerStop = _state == DictationState.Recording
            || (_state == DictationState.Starting && _startRequestDispatched);
        bool stateChanged = _state != DictationState.Idle;

        _sessionGeneration++;
        var (stream, cts) = ClearSessionLocked(suppressUpdates: true);
        _state = DictationState.Idle;

        return new AbortWork(stream, cts, needsServerStop, !disposing && stateChanged);
    }

    private async Task SendBestEffortAbortStopAsync()
    {
        try
        {
            await _client.StopRecordingAsync(DictationDefaults.AbortStopTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Abort cleanup is best-effort and must never surface unhandled exceptions.
        }
    }

    private void DisposeClientQuietly()
    {
        try { _client.Dispose(); }
        catch { }
    }

    private bool IsGenerationCurrent(long gen)
    {
        lock (_gate)
            return !_disposed && _sessionGeneration == gen;
    }

    private static bool MatchesRecordingId(string? activeRecordingId, string? eventRecordingId)
    {
        if (string.IsNullOrEmpty(activeRecordingId))
            return true;

        return string.Equals(activeRecordingId, eventRecordingId, StringComparison.Ordinal);
    }

    private void EmitState(DictationState state)
    {
        if (IsDisposed()) return;
        StateChanged?.Invoke(state);
    }

    private void EmitTextUpdate(DictationTextUpdate update)
    {
        if (IsDisposed()) return;
        TextUpdated?.Invoke(update);
    }

    private void EmitError(string message)
    {
        if (IsDisposed()) return;
        ErrorOccurred?.Invoke(message);
    }

    private bool IsDisposed()
    {
        lock (_gate)
            return _disposed;
    }

    private static void CancelAndDisposeCts(CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try { cts.Cancel(); }
        catch { }
        try { cts.Dispose(); }
        catch { }
    }

    private static async Task DisposeStreamQuietlyAsync(IDictationStream? stream)
    {
        if (stream == null) return;
        try { await stream.DisposeAsync().ConfigureAwait(false); }
        catch { }
    }

    private readonly record struct AbortWork(
        IDictationStream? StreamToClose,
        CancellationTokenSource? CtsToCancel,
        bool NeedsServerStop,
        bool StateChangedToIdle);
}
