# Canvas-anchored backgrounds

Delivery owner: Codex. Repository: schnepg1/CanvasWindowComposer. Base: 3651c520e0f2bc1f2b4a0df181991556293c8595. Branch: feat/canvas-backgrounds. Isolated checkout: /workspace/CanvasWindowComposer-backgrounds.

## Acceptance and design

Multiple persistent, canvas-coordinate image placements; reusable resources; managed copies by default and explicit external links; free or area-bound; deliberate editor with lock, geometry, aspect ratio, crop, Fill/Fit/Tile, opacity, dimming, order, replacement, undo/redo. Four offline original starter illustrations. Missing assets must preserve placement and remain replaceable. Bound backgrounds follow area geometry and detach on area deletion.

Inspect current code rather than relying on the lost October implementation. Main and advertised remote branch names checked on 2026-11-04: no background implementation found. Prior test reports are not evidence for this change.

The current renderer uses DWM thumbnails with a passive WPF surface above them. Backgrounds use a first WPF painting pass, clipped around DWM window rectangles; labels/UI paint afterward. This follows existing background-annotation clipping and avoids a renderer migration. Transparency inside thumbnail rectangles cannot reveal these backgrounds. Hidden desktop mode has no overview surface and is not claimed supported. Windows UI/native validation is essential before merge.

Resources and placements use a separate versioned file under AppConfig.ConfigDir, preserving existing workspace format. No workspace export/transfer function was found (performance diagnostic export is unrelated); a portable bundle remains an explicit gap.

## Progress and evidence

- Read AGENTS.md and architecture; created isolated worktree.
- Linux SDK 10.0.401 available from onboarding; baseline portable suite: 81 passed.
- Git read works. GitHub API CONNECT denied by proxy (403); api.github.com added to environment draft, pending runtime configuration. PR/review/CI operations currently blocked.
- Implementation in progress. No feature build, Windows integration, screenshots, automated review or merge claimed yet.

## Remaining delivery gates

Implement, run portable tests and Windows compilation/CI, inspect actual Windows UI and interactions (including DPI/multiple monitors, overlap, wide zoom), obtain automated review, fix findings, merge only with essential criteria verified, verify post-merge CI. Save useful checkpoints to remote early. No FPS or hardware improvement claimed without measurement.
