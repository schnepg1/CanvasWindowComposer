# Architecture Plan — Agent Model Routing & Team Assembly

Status: draft for worktree implementation
Target branch: `window_linking_workflows` (verify before merge)
Date: 2026-11-14

## 1. Problem

Canvas agents are launched as a fixed Codex process with no model identity. The
user wants:

1. A whitelist of models per CLI, shared by the herdr skill, the app and the
   orchestrator prompt.
2. A model/profile per agent card.
3. The orchestrator to receive its team roster and routing policy automatically.
4. Teams to be assembled per project, either from a preset or proposed by the
   orchestrator.
5. Running agents to be discoverable and visualized in the project zone they
   work in, even if the team was never declared up front.

## 2. Verified facts (2026-11-14, this machine)

| Lane | Pinnable | First prompt | Notes |
|---|---|---|---|
| `codex` | `-m`, `-c model_reasoning_effort=` | positional `PROMPT` | default `gpt-6.1-sol`, medium |
| `agy` (Antigravity CLI 1.5.9) | `--model`, `--effort` | `--prompt-interactive` | first run in a folder blocks on a trust dialog; `--dangerously-skip-permissions` allowed |
| `opencode` | `-m provider/model` | `--prompt` | TUI has **no** `--variant`; `run` has it |
| `freebuff` 0.2.36 | no | no | only `--cwd`, `--continue`; 12h/day, each started hour costs 1h × multiplier; continuing after expiry starts a new hour |
| `strata` | via opencode | `--prompt` | offline, iq2 quantized |

Catalog facts:

- opencode free models: 11 (Nemotron 3.5 Lightning is gone).
- agy models: `gemini-3.8-flash-high|medium|low`, `claude-sonnet-4-6`,
  `claude-opus-4-6-thinking`, `gpt-oss-120b-medium`. User whitelists **Gemini only**.
- codex whitelist: `gpt-6.1-luna`, `gpt-6.1-terra`, `gpt-6.1-sol`. Astra is forbidden.

herdr (`hc` 0.1.20) facts:

- `hc ls --json` returns `address`, `agent`, `cwd`, `name`, `state`, `status`,
  `turn`. **Labels are not returned**, only usable as filters.
- `hc run --label` accepts repeatable key=value; `hc label` sets them later.
- `hc run --bg` + `--name` (1–32 of `a-z0-9-_`) is the launch pattern. A 32-char
  card Guid fits exactly.
- Children end with their parent unless `--no-parent` / `--outlive-parent`.
- **Blocker:** `opencode` TUI launched in a herdr terminal renders but does not
  accept `hc send` / `hc keys` input (`turn: 0`, blank screen). Treat it as
  one-shot (`opencode run`) or as an app-WebView terminal until proven otherwise.

## 3. Source of truth

`C:\Users\ESCEHA\.agents\teams\models.json` (+ `ledger.json`), with
`C:\Users\ESCEHA\.agents\skills\herdr-model-routing\SKILL.md` reading it.

Consumers: the skill, the app (dropdown/validation), the orchestrator prompt.
The app must **vendor a copy** (embedded resource) and prefer the user file when
present, so a missing file never breaks the app.

No model id may be hardcoded in the skill prose or in C#.

## 4. Team model: intent vs presence

Three layers, deliberately separate:

1. **`TeamTemplate`** — optional, per project zone. Role → profile defaults.
   Stored beside `ProjectFolderPath`. Absent → global `role_defaults`.
2. **`Mission`** — a running team zone (`IsMission`, `AgentTaskId`) plus its
   cards. May start with one card (orchestrator) and grow at runtime.
3. **`Presence`** — observed from `hc ls`. This is the truth for "who is
   actually running, where".

### Answer to "must the team exist before?"

No. The minimum is a project binding plus one leader. Roles may be:

- declared up front (template),
- created by the orchestrator at runtime (spawn + label),
- discovered (running, unlabeled → generic agent card, no invented role).

### Precedence and rendering

Declared roster = intent. `hc ls` = truth. Render the union:

- declared + running → live card,
- declared + not running → idle card,
- running + undeclared → adopted card ("Unassigned" until adopted),
- terminal for another project → never rendered here.

The roster must never hide a running agent.

### Discovery contract

At launch, the app sets:

```
name   = <card guid N>            (32 chars, unique)
label  = canvas_managed=1
label  = role=<role>
label  = model=<profile-id>
label  = task=<taskId>
label  = project=<zoneId>
```

Because `hc ls --json` does not return labels, discovery uses:

1. `hc ls --json --all --label canvas_managed=1` → all managed terminals.
2. `name` → card Guid (existing `HerdrSessions.Snapshot` behavior).
3. `cwd` → project binding fallback.
4. Per-role filter queries only when the role is needed and the card is unknown
   (bounded: 8 roles).

### Folder → zone binding (priority)

1. `project=<zoneId>` label (immutable, survives `cd`) — preferred.
2. Normalized `cwd` prefix vs `ProjectFolderPath` (case-insensitive, separator-normalized).
3. Git repo root / worktree common dir, so `..\CanvasWindowComposer-<task>`
   maps to the Canvas project. Resolve via `git rev-parse --git-common-dir`
   from the terminal `cwd`.
4. Otherwise "Unassigned" (backpack tray), never auto-created.

### Spawn vs adopt

- **Zone-first (default):** card exists → launch → labels set → reconcile.
- **Agent-first (recovery):** agent launched outside Canvas but labeled
  `canvas_managed=1` → adopt into the matching project, creating the zone at
  its anchor only if the setting `AutoAdoptManagedAgents` is on.

Never auto-create zones for unlabeled terminals.

### Hooks

`~/.config/opencode/plugins/herdr-agent-state.js` and `~/.codex/hooks.json`
refine *state* (working/waiting/done). They are not identity or membership
sources; do not depend on their schemas.

## 5. Task breakdown

### Phase A — catalog and skill (done)

- A1. `models.json` with lanes, 16 profiles, role defaults, gates, budgets. Done.
- A2. `ledger.json` seed. Done.
- A3. `herdr-model-routing` skill + pointer from the local `herdr-core` skill. Done.
- A4. Reconcile with the cloud account skill `herdr-core-orchestration`
  (user must delete or rename it; two skills with the same purpose otherwise).

### Phase B — profile on the card

- B1. `CanvasAnnotation.AgentProfile` (string) + validation (charset/length),
  JSON ctor, persistence, migration default `""`.
- B2. `AgentProfileCatalog`: embedded `models.json` fallback + user override;
  parse, index by profile id, resolve role defaults.
- B3. `HerdrSessions.Start(profile, folder, card, prompt, projectId)`:
  per-lane argv, refuse unknown profile, set labels, `--outlive-parent`.
- B4. `MissionControlWindow`: profile dropdown, manual badge for `freebuff`,
  "Start agent" replaces "Start Codex".
- B5. `MissionCardLayer`: model chip; keep the 104×44 minimum-control threshold.
- B6. Tests: argv per lane, catalog validation, annotation round-trip. No native tests.

### Phase C — team template and prompt manifest

- C1. `WorkspaceZone.TeamTemplateJson` (role→profile), persistence + validation + tests.
- C2. `TeamManifest` builder (pure): template + cards + gates + budget → compact JSON.
- C3. Inline the manifest into the orchestrator's first prompt (≤4 KB). No temp files.
- C4. Presets: `cheap`, `balanced`, `native-safe` → role→profile maps; tests.

### Phase D — discovery, adoption, project binding (the new question)

- D1. Extend `HerdrSessions.Terminal` with `Agent`, `Name`, `State`, `Cwd`.
- D2. `ProjectBinder` (pure): zones + cwd + project label → zone id.
  Tests: prefix, case, separator normalization, longest match, nested projects,
  worktree→repo root.
- D3. `AgentPresence` model: cardId?, terminal, projectId?, role?, model?, state.
- D4. `Reconcile(cards, terminals, zones)` → actions `Attach | Adopt | Idle |
  Orphan | Ignore`. Pure, fully unit-tested (this is the core logic).
- D5. Discovery sweep service on the existing 5s timer; do not add a second timer.
- D6. Unassigned tray in the backpack/inventory with "Add to project".
- D7. Opt-in `AutoAdoptManagedAgents`: labeled agent without a card creates one
  at the project anchor (never for unlabeled terminals).
- D8. Worktree mapping helper (`git rev-parse --git-common-dir`) with a timeout
  and a cache; test with a fake worktree.
- D9. Label setting after launch (`hc label`) and a documented note that labels
  are filter-only in `hc ls --json`.
- D10. Reconcile when the orchestrator spawns a child: the child appears as
  adopted within one sweep; the card is created at the existing mission anchor.

## 6. Invariants

- One agent process per card Guid; two live terminals for one card is an error
  (already enforced in `Find`).
- Roster never overrides presence.
- No auto-creation for unlabeled terminals.
- `project=` label is set at launch and never inferred later.
- All path matching is case-insensitive and separator-normalized.
- No native tests required for Phases A–D. If any native assertion is added,
  it must run through `Tools\Run-Tests.ps1` under the named mutex.

## 7. Risks

- `hc ls` may gain labels in a later version; keep the label-filter path and
  treat a `labels` field as an optional enhancement.
- opencode TUI input is currently unreliable in herdr → orchestrator default is
  `agy` or `codex`, not opencode.
- Worktree paths do not equal project paths; repo-root resolution must be cached
  and must handle `git` missing.
- `--outlive-parent` semantics: verify that a child survives the orchestrator's
  turn without leaking processes after cleanup.
- Cloud account skill and local skill can diverge; only the local file is
  auditable.

## 8. Open questions

1. Should `AutoAdoptManagedAgents` default on or off?
2. Should an adopted agent inherit the project's `TeamTemplate` role, or stay
   "unassigned" until the user picks a role?
3. When the orchestrator proposes a roster, does it also propose profile ids
   (validated against `models.json`) or only roles?
4. Is freebuff ever allowed to be a *child* agent, or orchestrator-only?
