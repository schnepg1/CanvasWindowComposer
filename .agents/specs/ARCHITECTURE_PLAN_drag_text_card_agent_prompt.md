# ARCHITECTURE PLAN: Drag and Drop Text Card onto Agent Card as Prompt

## Problem Statement & Scope
Users frequently organize tasks, instructions, and context notes on the canvas using Text Cards (notes). Currently, starting or sending work to an Agent Card requires manually opening Mission Control, launching Codex, and retyping or copying the instructions. 

This feature enables direct spatial interaction on the canvas:
1. The user drags a text card and drops it onto an agent card.
2. Upon drop, a small Fluent WPF confirmation popup appears asking to confirm assigning the text card's content as a prompt to the agent card.
3. Hitting "Enter" immediately confirms and dispatches the prompt; hitting "Esc" or clicking Cancel aborts the action.
4. On confirmation:
   - The agent card stores and displays the prompt (persisted on `CanvasAnnotation.AgentPrompt`).
   - If Herdr Core / Codex is active or available, the prompt is sent to the live terminal stream via `hc send`, or passed to `codex.cmd` when starting a session.
   - The text card on the canvas remains in place at its pre-drag position (not visually obscuring the agent card).
5. The solution must ship cleanly, pass all serialized test suites, and clean up temporary state.

---

## Architectural Impact & Manifest

### Files Modified or Created
1. `Modules/CanvasView/Annotations.cs`:
   - Add `public string? AgentPrompt { get; init; }` to `CanvasAnnotation`.
   - Ensure immutability and JSON roundtrip serialization.
2. `Modules/CanvasView/CanvasSelection.cs`:
   - Extend `CanvasSelectionMove` record with `CanvasPoint? DropPoint = null`.
   - In `CanvasSelectionTool.PointerUp(CanvasPointer p)`: pass `p.World` as `DropPoint`.
3. `Source/Ai/HerdrSessions.cs`:
   - Add `SendPrompt(Guid card, string prompt, string? folder, CancellationToken cancellation)`.
   - Update `StartCodex` to accept an optional `string? prompt = null` argument and append to CLI args when launching Codex.
4. `Source/Overlays/Overview/AgentPromptConfirmationDialog.cs` (New):
   - A compact, Fluent WPF window (`FluentWindow` or `Window` with dark Fluent styling, matching `ZoneTheme`/`Wpf.Ui`).
   - Size: ~420x240 DIPs, `Topmost = true`, `WindowStartupLocation = CenterOwner`.
   - Displays agent target name, prompt preview, "Send Prompt" (Primary, `IsDefault = true`), and "Cancel" (`IsCancel = true`).
   - Handles `PreviewKeyDown` for instantaneous Enter (confirm) and Escape (cancel) response.
   - Strictly follows `AGENTS.md` WPF Window Ownership guidelines using `WindowInteropHelper` fallback.
5. `Source/Overlays/Overview/OverviewManager.Selection.cs`:
   - In `MoveSelection(CanvasSelectionMove move)`:
     - Detect if a single text card is dragged and dropped onto any `MissionAgentCard` on the current desktop.
     - When dropped onto an agent card: show `AgentPromptConfirmationDialog`.
     - If confirmed: update agent card's `AgentPrompt`, trigger `_herdrSessions.SendPrompt(...)`, and do not move the text card over the agent card.
     - If cancelled: revert the text card's position.
6. `Source/Overlays/Overview/MissionCardLayer.cs` & `MissionControlWindow.cs`:
   - Expose `Prompt => Layout.AgentPrompt` on `MissionAgentCard`.
   - Display active prompt in `MissionControlWindow` agent inspector when present.
7. `Tests/AgentPromptDropTests.cs` (New):
   - Automated tests covering `CanvasAnnotation.AgentPrompt` persistence, drop target detection, dialog lifecycle, and Herdr command construction.

---

## Atomic Task Checklist

- [ ] **Task 1: Model & Serialization Updates**
  - Add `public string? AgentPrompt { get; init; }` to `CanvasAnnotation` in `Modules/CanvasView/Annotations.cs`.
  - Add `CanvasPoint? DropPoint = null` to `CanvasSelectionMove` in `Modules/CanvasView/CanvasSelection.cs`.
  - Pass `p.World` as `DropPoint` in `CanvasSelectionTool.PointerUp`.
- [ ] **Task 2: HerdrSessions Prompt Integration**
  - Update `StartCodex` in `Source/Ai/HerdrSessions.cs` with optional `string? prompt = null`.
  - Add `SendPrompt(Guid card, string prompt, string? folder, CancellationToken cancellation)` to `HerdrSessions.cs`.
- [ ] **Task 3: Fluent WPF Confirmation Dialog**
  - Create `Source/Overlays/Overview/AgentPromptConfirmationDialog.cs`.
  - Implement compact layout (~420x240), dark fluent theme, agent name header, prompt preview.
  - Wire Enter (`IsDefault = true`, `PreviewKeyDown` Enter) to confirm, and Esc (`IsCancel = true`) to cancel.
- [ ] **Task 4: OverviewManager Drop Detection & Dispatch**
  - In `OverviewManager.Selection.cs` (`MoveSelection`), detect text card drop on `MissionAgentCard`.
  - Present `AgentPromptConfirmationDialog` using `CanvasPanelOwner` with Win32 HWND fallback.
  - On confirm: assign `AgentPrompt`, save to document, dispatch to Herdr, preserve note position.
- [ ] **Task 5: Inspector Display**
  - In `MissionControlWindow.cs`, display the active prompt if assigned to the agent card.
- [ ] **Task 6: Unit & Integration Tests**
  - Create `Tests/AgentPromptDropTests.cs` testing model persistence, drop detection, dialog Enter/Esc keys, and prompt dispatch.
- [ ] **Task 7: Test Suite Verification & Shipping**
  - Execute `Tools\Run-Tests.ps1 -Configuration Release -NoPrompt` under machine-wide named mutex.
  - Review diff against invariants.
  - Commit changes to `drag_text_card_agent_prompt`.

---

## Test & Verification Strategy
- **Unit Tests (`Category!=Native*`)**:
  - `AgentPrompt_Serialization_PreservesPromptText`
  - `DropDetection_IdentifiesAgentCardUnderPointer`
  - `ConfirmationDialog_EnterConfirms_EscapeCancels`
  - `HerdrSessions_SendPrompt_SelectsLiveTerminalOrLaunchesCodex`
- **Runner**:
  - Run via `powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-Tests.ps1 -Configuration Release -NoPrompt`.
  - Honors `Global\CanvasWindowComposer.Tests` named mutex.

---

## Invariants & Risk Analysis
- **WPF Window Ownership**: Use `WindowInteropHelper` fallback for `AgentPromptConfirmationDialog` owner to prevent `Cannot set Owner to unshown Window` exception.
- **Canvas Desktop Interference**: Ensure `CanvasDesktop` is closed before running tests.
- **Herdr Process Safety**: Handle missing `hc.exe` or `codex.cmd` gracefully without crashing the UI.
