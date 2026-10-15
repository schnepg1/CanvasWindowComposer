# AI Canvas Composer

The first feature slice turns a request and explicitly checked canvas notes into
editable annotations. The existing desktop/window manager continues to own native
windows; AI proposals have no process, file, network-tool or window-movement powers.

## User flow

1. Open interactive overview and click **AI Compose** on the annotation toolbar.
2. Enter an OpenAI API key and a model ID supporting Responses Structured Outputs.
   Saving the key is optional and uses Windows Credential Manager. API usage is
   charged to that API account, separately from a ChatGPT subscription.
3. Write a request. Check the specific notes to share. Nothing is selected by
   default except a note explicitly selected before opening the composer.
4. Generate. The request runs asynchronously with cancellation and a two-minute
   timeout. The canvas remains unchanged. A completed, validated response becomes
   translucent, editable-object previews on the canvas, framed beside the panel.
5. Apply to commit the proposal as one annotation-history step, or discard/refine
   the request. Undo removes the whole batch or restores all rewritten text.

Examples: "Create a three-step roadmap", "Turn these checked notes into a dependency
diagram", "Rewrite the selected note into a concise checklist". The model is not
given arbitrary app contents, screenshots, file paths or other desktop notes.

## Contracts and boundaries

- `ICanvasComposerProvider` returns a `ComposerPlan`, never executable instructions.
- The OpenAI adapter posts to the fixed public Responses endpoint with a strict
  JSON schema and `store:false`. It does not execute tool calls or save prompts.
- Semantic plan: nodes (note/rectangle/ellipse, text, color), graph edges, and
  text-only edits to explicitly shared existing notes. No generated coordinates.
- `CanvasComposer.Prepare` validates references, scope, enums, colors and limits;
  topological columns (stable grid fallback for cycles) place the nodes. A local
  free-space search avoids existing annotations and native window bounds. Arrows
  terminate at node boundaries. Labeled shapes render through the existing WPF
  layer and their labels can be edited with the normal text editor.
- `ComposerPreviewLayer` is transient and never writes `annotations.json`. Rewritten
  originals are masked while their proposed versions are visible.
- `AnnotationDocument.ApplyBatch` checks revision and desktop, validates the entire
  merged document, and makes one history entry/notification. Invalid batches have
  no partial effects. All manual edits and undo/redo advance the revision.
- Document changes discard proposals and refresh the checked-note list. Desktop
  changes, overview closure, Escape and tool switches close/cancel the composer.
  Late/cancelled responses cannot re-enable Apply. Camera pan/zoom does not change
  document revision; users may inspect a proposal in world space.
- HTTP responses are bounded to 1 MiB, requests to 8,000 prompt characters plus
  20 checked notes / 32,000 context characters. A proposal can contain 40 nodes,
  60 edges and 20 note edits, within the existing 2,000-annotation document limit.
  Refusals, incomplete responses and unsupported models fail without canvas edits.
- Only completed edits trigger existing atomic annotation persistence. Save failures
  leave the batch undoable in memory and surface the existing save warning.

## Build sequence delivered by this PR

1. Revisioned atomic document edits and provider-neutral semantic proposal/layout.
2. Bounded OpenAI API-key adapter and optional OS-managed key storage.
3. Composer form, checked-note context, cancellation and revision invalidation.
4. Temporary canvas rendering, framing and batch Apply/Undo integration.
5. Headless validation/history/layout tests, fake-HTTP provider tests and native
   overview lifecycle tests. No real provider request or paid usage is part of CI.

## Connection roadmap

Keep the proposal/preview/edit contract shared; authentication and transports are
separate adapters. The first PR exposes API-key access only. It does not pretend
that a ChatGPT subscription is an API key or that ACP provides model entitlement.

**Next adapter: Sign in with ChatGPT.** Evaluate the official OSS/local OAuth flow,
including explicit plan-usage consent, stable host identity, refresh/revocation,
account-specific model catalog and usage controls. This is a separate integration
from API keys: current plan-usage requests require streaming and local history and
have a distinct set of supported parameters/tools. No existing ChatGPT conversation
history is granted by signing in. Paid/hosted distribution needs its own access path.

**Then ACP.** Add an optional external-agent connection using a pinned compatible
agent runtime. Map session, streaming, cancellation and permission events to the
same UI. Expose tightly scoped canvas commands through a local MCP adapter; do
not substitute shell execution or direct JSON file edits for validated mutations.
For a Codex-only experience, compare direct App Server integration with ACP before
adding both transports. Negotiate capabilities; show only features the adapter
actually supports. Never silently switch from subscription usage to API billing.

## Subsequent product PRs

- Semantic workspace navigation and deterministic arrangement proposals through
  stable workspace item IDs and a separate window-action batch contract.
- Project-area context and a saved, user-editable handoff note for resuming work.
- Explicit file/browser integrations with source links on generated notes.
- Optional agent task cards with visible progress and results attached to an area.

Native windows, project areas and annotations have separate histories today. A
cross-domain arrangement feature must introduce a coordinated transaction/history
boundary; opening an external program is not reversible like moving a note.

## Official references (checked 15 October 2026)

- Responses Structured Outputs: https://developers.openai.com/api/docs/guides/structured-outputs
- ChatGPT OSS plan usage: https://developers.openai.com/siwc/token-sharing-open-source
- Plan-usage request constraints: https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations
- Account-specific model listing: https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference
- Codex App Server: https://learn.chatgpt.com/docs/app-server
- ACP introduction: https://agentclientprotocol.com/get-started/introduction
- Codex ACP adapter: https://github.com/agentclientprotocol/codex-acp

## Verification

Run the Windows locked restore, Release build, full tests and publish workflow.
Real API model access/billing, Windows Credential Manager policy and physical
mixed-DPI interaction require manual checks on a user's Windows installation.
No real API-key or ChatGPT sign-in test is claimed by the automated suite.
