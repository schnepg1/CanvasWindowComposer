# ARCHITECTURE PLAN: AI Composer Modernization, EventStream Bug Fix & UX Overhaul

**Target Repositories/Branches:** `schnepg1/CanvasWindowComposer` (`agent_orchestration_skills` base)
**Task Identifiers:**
- `ai-composer-transport`: EventStream SSE bug fix, JSON fallback, Accept headers, and multi-turn protocol (`Modules/Ai/`)
- `ai-composer-ui`: Fluent WPF UI modernization, canvas selection context UX, window decoupling, and settings persistence (`Source/Overlays/Overview/`, `Source/Ai/`, `Source/Application/`)

---

## 1. Problem Statement & Scope

1. **"ChatGPT did not return an event stream" Bug**:
   - `OpenAiCanvasComposerProvider.GenerateAsync` does not send `Accept: text/event-stream, application/json` headers.
   - `ComposerEventStream.ReadAsync` throws `InvalidDataException("ChatGPT did not return an event stream.")` whenever the endpoint or proxy returns `application/json` (e.g., buffered or non-streaming responses), even when HTTP 200 contains a valid completed proposal.
   - **Resolution**: Send explicit `Accept` headers. In `ComposerEventStream` / `OpenAiCanvasComposerProvider`, seamlessly fall back to parsing `application/json` via `BoundedHttp.ReadJsonAsync`.

2. **Subscription / Mode Reset on Reopening**:
   - `AiConnectionPanel` hardcodes `_source.SelectedIndex = 0` (OpenAI API key) on creation.
   - Neither the source mode, the active ChatGPT account ID, nor the chosen model ID are saved across window invocations.
   - **Resolution**: Introduce persistent AI composer settings (`LastConnectionSource`, `LastAccountId`, `LastModelId`) loaded on initialization and saved on selection change.

3. **Window Lifecycle & "Popup" Behavior**:
   - `CanvasComposerWindow` sets `ShowInTaskbar = false; TopMost = true;` and is tied to the overview pass window (`form.Show(owner)`). Exiting zoom/overview mode or clicking away invokes `CancelCanvasComposer()`, closing the window.
   - Users cannot Alt+Tab to it, search for it, or keep it open side-by-side with reference material.
   - **Resolution**: Set `ShowInTaskbar = true`, modernize window styling, and decouple the window lifecycle so it behaves as an independent, accessible desktop tool window.

4. **Canvas Selection Context UX ("Diagram objects list makes no sense")**:
   - Currently, a giant WinForms `CheckedListBox` loads *every* annotation and window reference on the entire canvas, cluttering the UI with cryptic identifiers like `[Rectangle]`, `[Ellipse]`.
   - **User Requirement**: *"When someone wants to select them as context to have them changed, they should be selected on the canvas, and then the selected ones should appear in a selection box. We don't need a list where everything on the canvas is in the list; that is very unpractical."*
   - **Resolution**: Remove the all-canvas list. The context box only displays items that are currently selected on the canvas. If nothing is selected, display a clear, unobtrusive placeholder ("No canvas items selected. Select notes or shapes on the canvas to include them as context, or write a prompt to generate a new diagram.").

5. **Modern Fluent UI (`Wpf.Ui`) Upgrade**:
   - Rebuild the AI Composer interface leveraging the project's existing `WPF-UI` 4.3.0 library:
     - Fluent window styling, rounded corners, clean theme-aware palette.
     - Modern Segmented / Tab control for switching between "ChatGPT Plan" and "OpenAI API Key".
     - Fluent multi-line `Wpf.Ui.Controls.TextBox` with clear button.
     - Smooth `Wpf.Ui.Controls.ComboBox` for models and accounts.
     - Starter prompt chips (`Cloud Architecture`, `CI/CD Pipeline`, `Microservices`, `User Funnel`, `Roadmap`).
     - Progress ring with live character count during generation.
     - Proposal summary inspector card before applying to canvas.

---

## 2. Parallel Worktree Strategy (Zero File Overlap)

| Worktree | Branch | Scope | Touched Paths |
| :--- | :--- | :--- | :--- |
| `..\CanvasWindowComposer-ai-composer-transport` | `agent/ai-composer-transport` | EventStream bug fix, JSON fallback, Accept headers, tests | `Modules/Ai/`, `Tests/Ai/` |
| `..\CanvasWindowComposer-ai-composer-ui` | `agent/ai-composer-ui` | Fluent UI, Canvas selection context, Window decoupling, Settings persistence | `Source/Ai/`, `Source/Overlays/Overview/`, `Source/Application/`, `Tests/` |

---

## 3. Atomic Task Breakdown

### Track 1: AI Transport & Protocol (`agent/ai-composer-transport`)
- [ ] **Task 1.1**: Add `Accept: text/event-stream, application/json` headers to `OpenAiCanvasComposerProvider.GenerateAsync`.
- [ ] **Task 1.2**: Update `ComposerEventStream.ReadAsync` / `OpenAiCanvasComposerProvider`: if response `ContentType?.MediaType` is `application/json` (or non-SSE), read and return standard JSON via `BoundedHttp.ReadJsonAsync`.
- [ ] **Task 1.3**: Add unit tests in `Tests/Ai/CanvasComposerProviderTests.cs` covering:
  - Streaming SSE with progress reporting.
  - Graceful fallback when endpoint returns `application/json` with HTTP 200.
  - Proper error extraction when non-streaming response returns an error object.
- [ ] **Task 1.4**: Run `dotnet test Tests/Ai/CanvasDesktop.Ai.Tests.csproj -c Release` and verify 100% pass.

### Track 2: UI Modernization, Canvas Context & Persistence (`agent/ai-composer-ui`)
- [ ] **Task 2.1**: Implement `AiComposerSettings` persistence in `Source/Ai/` or `AppConfig.cs` for `LastConnectionSource` ("OpenAI API key" vs "ChatGPT plan"), `LastAccountId`, and `LastModel`.
- [ ] **Task 2.2**: Update `CanvasComposerWindow`:
  - Enable `ShowInTaskbar = true`.
  - Allow user to Alt+Tab to the window and search for it.
  - Remove full-canvas item dump. Implement canvas selection synchronization: ONLY items selected on the canvas appear in the context box.
  - Provide clear empty state when no canvas items are selected.
- [ ] **Task 2.3**: Upgrade UI components to Fluent `Wpf.Ui`:
  - Segmented control / Tab buttons for Source selection.
  - Fluent prompt TextBox with placeholder.
  - Starter prompt chips (`Cloud Architecture`, `CI/CD Pipeline`, `Microservices`, `User Funnel`, `Roadmap`).
  - Fluent ComboBoxes for accounts and models.
  - Status indicator & progress ring during generation.
  - Clean proposal summary card with node/edge counts.
- [ ] **Task 2.4**: Update `OverviewManager.Composer.cs`:
  - Allow composer to remain open independently without being dismissed when overview mode shifts.
  - Keep canvas selection dynamically updating the composer's context box.
- [ ] **Task 2.5**: Compile and verify tests via `dotnet build CanvasDesktop.slnx -c Release`.

---

## 4. Verification & Merge Strategy
1. Track 1 runs `dotnet test Tests/Ai/CanvasDesktop.Ai.Tests.csproj -c Release`.
2. Track 2 runs `dotnet build CanvasDesktop.slnx -c Release` and relevant tests.
3. Merge Track 1 into `agent_orchestration_skills`, then merge Track 2.
4. Clean up temporary worktrees and branches per AGENTS.md.
