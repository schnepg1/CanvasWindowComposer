# Architecture Plan: LRU Image Cache Eviction for Rapid Zoom Flicks

**Task ID:** `lru-image-cache-eviction`  
**Phase:** Phase 3 of Smooth Canvas Zooming & Panning  
**Target Branch:** `main`  
**Status:** `PLAN_READY`  
**Architect:** System Architect Subagent  

---

## 1. Executive Summary & Problem Context

CanvasWindowComposer supports fluid navigation across infinite workspaces spanning zoom scales from 5% (extreme distant overview) to 200% (close-up detailed inspection). In earlier phases:
- **Phase 1** implemented dynamic wireframe Level-of-Detail (LOD) at distant zoom levels (`zoom < 0.25` or `projected size < 40 px`) during active motion, eliminating text layout allocations and complex drawing calls while moving.
- **Phase 2** implemented post-motion settle staggering for DWM thumbnails, partitioning thumbnail unsuppression into concentric batches outward from the cursor focal point to eliminate the 1-frame DWM IPC hitch when motion halts.

### The Problem in Phase 3
When users rapidly flick the mouse wheel between distant (5%) and near (200%) zoom tiers:
1. **Unbounded Placeholder Bitmap Retention:**
   In [`PlaceholderForm.cs`](file:///Source/Workspace/WorkspaceForms.cs), each placeholder card instance stores a local reference `_overviewImage` (`BitmapSource`). During zoom-in, cards transition from Simple mode into Detailed mode and invoke `GetOverviewImage(dimension)` at increasing resolutions (256, 512, 768, 1024 px). Each upgrade creates a new unmanaged WIC/DirectX-backed `BitmapSource` (up to 4 MB per 1024x1024 card). When the user zooms further into Live mode (`OverviewCardMode.Live`), the card is occluded by the live DWM thumbnail, but `PlaceholderForm._overviewImage` is never released. In dense workspaces (30–60 windows), this retains 120–240+ MB of high-resolution bitmaps indefinitely.
2. **Icon & GDI Handle Duplication:**
   Every [`PlaceholderArtwork`](file:///Source/Workspace/PlaceholderArtwork.cs) control independently extracts a 256x256 GDI `Bitmap` and creates a 128x128 frozen WPF `BitmapSource`. For applications with multiple windows (e.g., 20 VS Code or Chrome windows), 20 identical copies of GDI bitmaps and WPF textures are retained simultaneously. Furthermore, [`DrawerAppIcon.cs`](file:///Source/Overlays/Temporary/DrawerAppIcon.cs) uses a crude dictionary that calls `Cache.Clear()` when exceeding 128 entries, causing sudden periodic CPU/disk hitches when all icons are re-extracted.
3. **$O(N)$ Eviction & Concurrency Defects in `WindowPreviewCache`:**
   In [`WindowPreviewCache.cs`](file:///Source/Core/WindowPreviewCache.cs), `EvictOldest()` performs an $O(N)$ linear loop over entries. More critically, `_used` memory bytes are mutated outside `lock (_workerGate)` in `DrainCompleted()`, while read under the lock in `ImageBytes`, causing data races. Evicted entries also call `Surface?.Dispose()` (an off-screen WinForms HWND) directly while inside worker synchronization locks, creating UI thread lock contention.
4. **Stale Image Churn During Rapid Flicks:**
   When a user performs rapid zoom flicks, the camera crosses multiple zoom tiers in tens of milliseconds. [`OverviewCardImageQueue`](file:///Source/Overlays/Overview/OverviewCardImageQueue.cs) continues processing queued image generation requests for obsolete intermediate dimensions (e.g. 256 px) even though the camera has already zoomed in to 1024 px or entered Live mode. This churn wastes CPU cycles and floods the Gen 2 GC heap with short-lived large byte arrays.

### Solution Goals
1. Implement a high-performance, thread-safe, $O(1)$ bounded LRU cache (`LruMemoryCache<TKey, TValue>`) with dual constraints: maximum item capacity and maximum memory byte budget.
2. Centralize placeholder snapshot storage in an `OverviewCardImageCache`, decoupling bitmap lifetime from `PlaceholderForm` instances, capping total card bitmap memory (default 48 MB), and demoting Live cards to be evicted first under memory pressure.
3. Deduplicate icon extraction across all cards and drawer items via a shared `PlaceholderArtworkCache`, reducing icon memory and GDI handle count by >85%.
4. Modernize `WindowPreviewCache` to use $O(1)$ LRU eviction, eliminate worker-lock races on `_used`, and decouple UI HWND surface disposal from worker gate locking.
5. Enhance `OverviewCardImageQueue` to supersede/cancel obsolete zoom-tier requests when the camera accelerates past intermediate dimensions.

---

## 2. Codebase Reconnaissance & Audit

The following table summarizes all image, bitmap, and preview caching points identified during codebase analysis:

| Component | File | Stored Types | Current Retention / Eviction Policy | Issues Identified |
|---|---|---|---|---|
| **Placeholder Overview Images** | `Source/Workspace/WorkspaceForms.cs` (`PlaceholderForm`) | `BitmapSource? _overviewImage` | Retained per `PlaceholderForm` field. Only released if `simple` is true or card is offscreen. | Unbounded across cards; never released in Live mode; upgrades abandon old bitmaps to GC; 120–240 MB churn in dense workspaces. |
| **Card Artwork & Icons** | `Source/Workspace/PlaceholderArtwork.cs` | `Bitmap? _symbol`<br>`BitmapSource? _overviewLogo` | Retained per `PlaceholderArtwork` instance for card lifetime. | No deduplication across same executable; GDI bitmaps kept per instance; redundant disk icon extractions. |
| **Drawer App Icons** | `Source/Overlays/Temporary/DrawerAppIcon.cs` | `Dictionary<string, BitmapSource?> Cache` | Naive `if (Cache.Count >= 128) Cache.Clear();` | Complete cache flush creates sudden stutter; separate from `PlaceholderArtwork` cache. |
| **Automatic Parking Previews** | `Source/Core/WindowPreviewCache.cs` | `Bitmap Image`<br>`ICachedPreviewSurface? Surface` (`CachedPreviewWindow` HWND) | 32 MB default budget, 64 entries. $O(N)$ linear scan on eviction (`EvictOldest`). | $O(N)$ eviction latency; race condition on `_used` between background thread and UI thread; HWND surface disposed under worker lock. |
| **Overview Card Transitions** | `Source/Overlays/Overview/OverviewCardTransitions.cs` | `ImageSource? Image`<br>`ImageSource? Logo` in `Entry` | Retained while transitioning between Simple, Detailed, and Live modes. Cleaned on transition completion. | Holds references to `BitmapSource` during transition; does not own or limit memory independently. |
| **Background Image Cache** | `Source/Backgrounds/BackgroundAssets.cs` (`BackgroundImageCache`) | `BitmapSource? Image` | 128 MB maximum bytes, 512 entries. LRU clock sequence scan. | Specialized for desktop background wallpapers (not window cards), but demonstrates good memory accounting patterns (`Width * Height * 4`). |
| **Desktop State Switching** | `Source/Application/DesktopStateCache.cs` | `Dictionary<Guid, CanvasState>` | Retains canvas camera coordinates per virtual desktop. | Switches destroy and recreate `PlaceholderForm` instances, causing repeated bitmap churn on desktop changes. |

---

## 3. Architecture & Component Design

```
+-------------------------------------------------------------------------------------------------+
|                                    LruMemoryCache<TKey, TValue>                                 |
|                               (Generic, O(1), Thread-Safe, Dual-Budget)                          |
| - Capacity: Max Items                                                                           |
| - Budget: Max Bytes (GetEntryBytes func)                                                        |
| - Fast O(1) Dictionary + Doubly-Linked List (MRU Head -> LRU Tail)                              |
| - Eviction Callback: OnEntryEvicted(TKey, TValue)                                               |
+-------------------------------------------------------------------------------------------------+
             ^                                           ^                              ^
             |                                           |                              |
+--------------------------+               +----------------------------+  +--------------------------+
|  OverviewCardImageCache  |               |   PlaceholderArtworkCache  |  |    WindowPreviewCache    |
|  (Placeholder Snapshots) |               |     (Icons & Logos)        |  |   (Automatic Parking)    |
|--------------------------|               |----------------------------|  |--------------------------|
| - Key: Guid CardId       |               | - Key: string ExeOrFolder  |  | - Key: HWnd/Token        |
| - Budget: 48 MB (~12-48) |               | - Budget: 16 MB (~128)     |  | - Budget: 32 MB (~64)    |
| - Upgrade in-place       |               | - Deduplicates Chrome, etc.|  | - O(1) LRU list          |
| - Demote on Live mode    |               | - Auto-disposes GDI Bitmap |  | - Thread-safe _used      |
| - Decoupled from Forms   |               | - Shared with DrawerAppIcon|  | - UI-thread HWND cleanup |
+--------------------------+               +----------------------------+  +--------------------------+
             |                                           |                              |
             v                                           v                              v
[ PlaceholderForm.GetOverviewImage ]         [ PlaceholderArtwork / Drawer ]    [ WindowManager.Parking ]
```

### 3.1. Generic Thread-Safe $O(1)$ LRU Cache: `LruMemoryCache<TKey, TValue>`
File: `Source/Core/LruMemoryCache.cs`

A reusable, self-contained, high-performance container:
- **Time Complexity:** $O(1)$ for `TryGet`, `Set`, `Touch`, `Remove`, and `EvictOldest`.
- **Data Structure:** 
  - `Dictionary<TKey, LinkedListNode<LruNode>> _map`
  - `LinkedList<LruNode> _list` (Head = Most Recently Used, Tail = Least Recently Used).
  - `LruNode` stores `TKey Key`, `TValue Value`, and `long Bytes`.
- **Dual Bounds:**
  - `MaxCapacity`: Maximum count of items allowed.
  - `MaxMemoryBytes`: Maximum aggregate bytes allowed.
  - Calculated via caller-supplied `Func<TValue, long> getBytes`.
- **Thread Safety:**
  - Fine-grained lock (`object _sync = new()`) guarding the linked list and dictionary.
  - Evictions populate a local list of evicted entries; `OnEntryEvicted(key, value)` and `(value as IDisposable)?.Dispose()` are executed **outside** the lock to eliminate lock inversion and UI thread deadlocks.
- **Demotion Support:**
  - `Demote(TKey key)`: Moves an entry directly to the **tail** (LRU position) of the linked list without removing it. This ensures that when a window enters Live mode, its cached snapshot becomes the immediate candidate for eviction if memory pressure occurs.

### 3.2. Centralized Placeholder Image Cache: `OverviewCardImageCache`
File: `Source/Overlays/Overview/OverviewCardImageCache.cs`

- **Purpose:** Centralize all overview snapshot bitmaps (`BitmapSource`) for placeholder forms.
- **Key:** `Guid CardId` (the Workspace item ID).
- **Value Record:**
  ```csharp
  internal sealed record CachedCardImage(
      BitmapSource Image,
      int Dimension,
      int Dpi,
      Size Size,
      long Bytes);
  ```
- **Configuration Defaults:**
  - Default Budget: `OverviewZoomDefaults.DefaultCardImageBudgetBytes` = 48 MB (`48L * 1024 * 1024`).
  - Default Capacity: `OverviewZoomDefaults.DefaultCardImageMaxCount` = 64 items.
- **Integration with `PlaceholderForm`:**
  - `PlaceholderForm.GetOverviewImage(dimension)` checks `OverviewCardImageCache.TryGet(item.Id)`.
  - If a cached image exists with matching dimension, DPI, and size, it returns immediately in $O(1)$ time.
  - If missing or upgraded: renders via GDI/WIC, calls `OverviewCardImageCache.Set(item.Id, entry)`, and returns the frozen `BitmapSource`.
  - When `PlaceholderForm` is disposed or an item is deleted, it calls `OverviewCardImageCache.Remove(item.Id)`.
  - `PlaceholderForm.CachedOverviewImage` simply reads `OverviewCardImageCache.TryGet(item.Id)`.

### 3.3. Shared Icon and Logo Cache: `PlaceholderArtworkCache`
File: `Source/Workspace/PlaceholderArtworkCache.cs`

- **Purpose:** Eliminate duplicate icon extractions and duplicate GDI/WPF bitmap allocations across windows that share the same executable or folder type.
- **Key:** `string CacheKey` (e.g. `item.IsFolder ? "folder" : item.ExecutablePath.ToLowerInvariant()`).
- **Value Record:**
  ```csharp
  internal sealed record SharedArtwork(
      Bitmap? Symbol,
      BitmapSource? OverviewLogo,
      long Bytes) : IDisposable
  {
      public void Dispose() => Symbol?.Dispose();
  }
  ```
- **Capacity:** Capped at 128 distinct application/folder icons (approx. 16 MB max).
- **Clean Cleanup:** When an entry is evicted from `LruMemoryCache`, its `Dispose()` method is invoked, freeing the native GDI `Bitmap` handle.
- **Unification:** Both `PlaceholderArtwork.LoadSymbol / CreateOverviewLogo` and `DrawerAppIcon.Create` query `PlaceholderArtworkCache.GetOrCreate(key, factory)`, completely replacing the primitive `DrawerAppIcon.Cache.Clear()` logic.

### 3.4. Hardening & Modernizing `WindowPreviewCache`
File: `Source/Core/WindowPreviewCache.cs`

- **$O(1)$ Eviction Queue:** Replace the $O(N)$ loop in `EvictOldest()` with an internal `LinkedList<Entry>` or `LruMemoryCache`.
- **Thread Safety & Race Elimination:**
  - Ensure all updates to `_used` and `_entries` are synchronized strictly under `_workerGate`.
  - In `DrainCompleted()`, ensure `_used += entry.Bytes` occurs inside `lock (_workerGate)` so `ImageBytes` never returns corrupted or partial sums.
- **Safe Surface Teardown:**
  - Decouple `CachedPreviewWindow` (HWND) disposal from background capture locks. When an entry is evicted, its HWND surface disposal is dispatched cleanly on the UI thread without holding the background capture lock.
- **Motion Suppression:**
  - During `OverviewManager.MotionActive == true` or `IsZoomSettling == true`, background capture scheduling via `RequestCapture` is deferred to prevent GDI surface churn during rapid mouse wheel bursts.

### 3.5. Rapid Zoom Flick Request Superseding in `OverviewCardImageQueue`
File: `Source/Overlays/Overview/OverviewCardImageQueue.cs`

- **Superseding Requests:** If a card ID already has a pending request in `_pending` at dimension $D_1$ (e.g. 256), and a new request arrives for dimension $D_2$ (e.g. 1024), the older request is replaced in-place rather than running sequentially.
- **LOD Pruning:** In `EndFrame()`, any pending request whose card has transitioned to Wireframe LOD (`ShouldUseWireframeLod == true`) or Live mode is canceled immediately, preventing useless rasterization.

---

## 4. Risk & Invariant Analysis

Audit against repository rules in [`AGENTS.md`](file:///AGENTS.md) and [`Docs/smooth-zooming-and-panning.md`](file:///Docs/smooth-zooming-and-panning.md):

1. **Desktop Isolation & Named Mutex:**
   - None of the caching components touch real desktop HWNDs or D3D composition directly.
   - All unit tests for `LruMemoryCache`, `OverviewCardImageCache`, `PlaceholderArtworkCache`, and `OverviewCardImageQueue` are fast non-native tests (`Category!=Native*`) and run without requiring the `Global\CanvasWindowComposer.Tests` mutex.
   - Any tests verifying `WindowPreviewCache` with `CachedPreviewWindow` and DWM registration remain tagged under `[Trait("Category", "NativePreview")]` and must run under the runner mutex.
2. **WPF / STA Thread Affinity:**
   - WPF `BitmapSource` instances are immutable once `.Freeze()` is called, enabling safe concurrent reading from any thread.
   - WinForms controls (`PlaceholderForm`, `PlaceholderArtwork`) and GDI `Bitmap` instances must only be constructed, painted, and disposed on the UI STA thread.
   - Eviction callbacks from `LruMemoryCache` that dispose GDI bitmaps or HWNDs must occur on the UI STA thread.
3. **Resource Management (GDI & Unmanaged Handles):**
   - Windows restricts each process to 10,000 GDI handles. Creating and discarding GDI `Bitmap` objects without `.Dispose()` leaks GDI handles and crashes the desktop.
   - The LRU cache guarantees that every evicted GDI bitmap is explicitly disposed immediately upon eviction.
4. **Motion Invariants:**
   - Zooming and panning must maintain 60–144 FPS. Cache lookups during mouse moves or wheel ticks must be $O(1)$ and never trigger synchronous GDI rasterization, disk I/O, or `Dispatcher.Invoke(Render)`.

---

## 5. Atomic Implementation Checklist

The implementation is broken into 8 sequential, atomic tasks:

### Task 1: Generic Thread-Safe `LruMemoryCache<TKey, TValue>`
- **Files to Create:** `Source/Core/LruMemoryCache.cs`, `Tests/LruMemoryCacheTests.cs`
- **Signatures:**
  ```csharp
  internal sealed class LruMemoryCache<TKey, TValue> : IDisposable where TKey : notnull
  {
      public LruMemoryCache(int maxCapacity, long maxMemoryBytes, Func<TValue, long> getBytes, Action<TKey, TValue>? onEvicted = null);
      public int Count { get; }
      public long TotalBytes { get; }
      public int MaxCapacity { get; set; }
      public long MaxMemoryBytes { get; set; }
      public bool TryGet(TKey key, out TValue? value);
      public void Set(TKey key, TValue value);
      public bool Demote(TKey key);
      public bool Remove(TKey key);
      public void Clear();
      public void Dispose();
  }
  ```
- **Tests to Add:**
  - `LruMemoryCacheTests.EvictsLeastRecentlyUsedWhenCapacityExceeded()`
  - `LruMemoryCacheTests.EvictsLeastRecentlyUsedWhenByteBudgetExceeded()`
  - `LruMemoryCacheTests.DemoteMovesEntryToEvictionHead()`
  - `LruMemoryCacheTests.DisposesEvictedDisposableEntries()`
  - `LruMemoryCacheTests.ConcurrentReadersAndWritersDoNotCorruptState()`

### Task 2: Shared Icon & Logo Deduplication (`PlaceholderArtworkCache`)
- **Files to Create:** `Source/Workspace/PlaceholderArtworkCache.cs`, `Tests/PlaceholderArtworkCacheTests.cs`
- **Files to Update:** `Source/Workspace/PlaceholderArtwork.cs`, `Source/Overlays/Temporary/DrawerAppIcon.cs`
- **Behavior:**
  - Create `PlaceholderArtworkCache` wrapping `LruMemoryCache<string, SharedArtwork>`.
  - In `PlaceholderArtwork.UpdateItem`, resolve symbol and overview logo through `PlaceholderArtworkCache.GetOrCreate(...)`.
  - In `DrawerAppIcon.Create`, replace static `Dictionary` with `PlaceholderArtworkCache`.
- **Tests to Add:**
  - `PlaceholderArtworkCacheTests.DeduplicatesIdenticalExecutableIcons()`
  - `PlaceholderArtworkCacheTests.DisposesGdiBitmapOnEviction()`
  - `PlaceholderArtworkCacheTests.FolderArtworkIsShared()`

### Task 3: Bounded `OverviewCardImageCache` & Zoom Defaults Configuration
- **Files to Create:** `Source/Overlays/Overview/OverviewCardImageCache.cs`, `Tests/OverviewCardImageCacheTests.cs`
- **Files to Update:** `Source/Application/OverviewZoomDefaults.cs`, `Source/Application/AppConfig.cs`
- **Behavior:**
  - Define `OverviewZoomDefaults.DefaultCardImageBudgetBytes = 48 * 1024 * 1024` (48 MB).
  - Define `OverviewZoomDefaults.DefaultCardImageMaxCount = 64`.
  - Implement `OverviewCardImageCache` storing `CachedCardImage` records.
  - Expose `Demote(Guid cardId)` to deprioritize cards entering Live mode.
- **Tests to Add:**
  - `OverviewCardImageCacheTests.CachesAndRetrievesCardBitmaps()`
  - `OverviewCardImageCacheTests.EnforcesByteBudgetDuringMultipleSimulatedZoomGenerations()`
  - `OverviewCardImageCacheTests.DemotedLiveCardsAreEvictedBeforeActiveDetailedCards()`

### Task 4: Decouple `PlaceholderForm` and Integrate `OverviewCardImageCache`
- **Files to Update:** `Source/Workspace/WorkspaceForms.cs` (`PlaceholderForm`)
- **Behavior:**
  - Replace local `_overviewImage` field with queries to `OverviewCardImageCache`.
  - When `GetOverviewImage(dimension)` is called: check cache; if miss or dimension upgrade, render GDI bitmap, convert to `BitmapSource`, freeze, and store in `OverviewCardImageCache`.
  - When `PlaceholderForm.Dispose()` is called, remove card entry from cache.
  - Update `CachedOverviewImage` property to return image from cache.
- **Tests to Update:**
  - `OverviewCardImageRenderingTests.OverviewImagesAreBoundedCachedForPanningAndTextRowsFitTheirContent`

### Task 5: Refactor `WindowPreviewCache` to $O(1)$ LRU & Fix Threading
- **Files to Update:** `Source/Core/WindowPreviewCache.cs`, `Tests/WindowPreviewTests.cs`
- **Behavior:**
  - Replace $O(N)$ linear loop in `EvictOldest()` with an internal $O(1)$ LRU queue.
  - Enclose `_used += entry.Bytes` in `DrainCompleted()` within `lock (_workerGate)`.
  - Ensure `Surface?.Dispose()` executes on UI STA thread outside worker locks.
- **Tests to Add / Verify:**
  - Run existing `WindowPreviewTests` to confirm all 14 tests pass.
  - Add `WindowPreviewTests.ThreadSafeUsedAccountingUnderRapidDrain()`

### Task 6: Rapid Zoom Flick Request Superseding in `OverviewCardImageQueue`
- **Files to Update:** `Source/Overlays/Overview/OverviewCardImageQueue.cs`
- **Behavior:**
  - In `RequestImage(Guid id, long priority, Action prepare)`: if `_pending.ContainsKey(id)`, update request in-place with new priority and preparation action.
  - In `EndFrame()`: cancel pending requests for cards that are no longer detailed or have transitioned to wireframe LOD.
- **Tests to Add:**
  - `OverviewCardImageQueueTests.NewerZoomDimensionRequestSupersedesStaleDimension()`
  - `OverviewCardImageQueueTests.WireframeLodTransitionPrunesPendingImageRequests()`

### Task 7: Integrate Demotion in `OverviewManager.Zones.cs` & `OverviewCardTransitions.cs`
- **Files to Update:** `Source/Overlays/Overview/OverviewManager.Zones.cs`, `Source/Overlays/Overview/OverviewCardTransitions.cs`
- **Behavior:**
  - When a card's transition settles into `OverviewCardMode.Live`, invoke `_cardImagesCache.Demote(cardId)`.
  - When motion is active (`OverviewManager.MotionActive == true`), throttle `OverviewCardImageQueue.Process()` to 0 or 1 item per frame to prioritize input responsiveness.
- **Tests to Add:**
  - Verify zoom transition tests pass (`FastZoomSimplificationTests`, `DynamicLodTests`).

### Task 8: Full Regression Testing & Documentation Updates
- **Files to Update:** `Docs/smooth-zooming-and-panning.md`
- **Behavior:**
  - Document Phase 3 in `Docs/smooth-zooming-and-panning.md` under section 3.10: "Bounded LRU Image Cache Eviction for Rapid Zoom Flicks".
  - Run full non-native test suite (1,390+ tests) to guarantee zero regressions.

---

## 6. Test & Verification Plan

### Targeted Unit Test Commands:
```powershell
# Run the newly created LRU cache and image queue tests:
dotnet test Tests/CanvasDesktop.Tests.csproj -c Release -p:CanvasSkipTestPreflight=true --filter "FullyQualifiedName~LruMemoryCacheTests|FullyQualifiedName~OverviewCardImageCacheTests|FullyQualifiedName~PlaceholderArtworkCacheTests|FullyQualifiedName~OverviewCardImageQueueTests|FullyQualifiedName~WindowPreviewTests"
```

### Complete Non-Native Test Suite (Serialized under runner):
```powershell
# Verify entire solution non-native tests:
dotnet test Tests/CanvasDesktop.Tests.csproj -c Release -p:CanvasSkipTestPreflight=true --filter "Category!=NativeRendering&Category!=NativeParking&Category!=NativePlacement&Category!=NativePreview"
```

---

## 7. Edge Cases & Failure Modes

| Edge Case / Failure Mode | Root Cause / Condition | Mitigation & Recovery Logic |
|---|---|---|
| **Rapid Zoom Flick Flood** | User spins mechanical wheel 10 notches in 50 ms. | Intermediate requests supersede previous requests in `OverviewCardImageQueue`; Wireframe LOD suppresses requests below 25% zoom; LRU cache enforces strict 48 MB ceiling. |
| **GDI Handle Exhaustion** | Hundreds of distinct window icons extracted over a long session. | `PlaceholderArtworkCache` caps distinct icons at 128; evictions immediately call `Symbol.Dispose()`, keeping GDI handle count stable under 200. |
| **Live Thumbnail Overlay Eviction** | Window is zoomed in to Live mode; user pans across canvas. | Card is demoted in LRU cache so its snapshot is evicted first. If zoomed back out to Detailed mode, card falls back smoothly to Simple card layout until background image prepares. |
| **Out-of-Memory on Enormous Window Cards** | Rare application window with 8K x 8K virtual bounds. | `OverviewCardImageQueue.ImageDimension` caps maximum dimension at 1024 px; memory per entry capped at 4.19 MB; cache byte budget check prevents single image from exceeding budget. |
| **Multi-Thread Race on Preview Worker** | Background `PrintWindow` completes right as overview closes. | `DrainCompleted` verifies `_disposed` and observed identity under `_workerGate`; discarded bitmap is disposed immediately on worker thread. |
