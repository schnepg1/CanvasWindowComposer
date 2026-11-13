# Architecture Plan: Asynchronous Browser Tab & Web App Logo Extraction with Host Browser Badging

**Task ID:** `extract-browser-tab-logos`  
**Target Branch:** `extract_browser_tab_logos`  
**Status:** `PLAN_READY`  
**Architect:** System Architect Subagent  

---

## 1. Executive Summary & Problem Context

CanvasWindowComposer turns the Windows desktop into an infinite, pannable, zoomable canvas. When users work with multiple web-based destinations (such as YouTube, GitHub, Jira, Notion, Figma, Google Docs, etc.) across Chrome, Edge, or Firefox:
1. **Visual Homogeneity:**
   Currently, [PlaceholderArtwork.LoadSymbol](file:///Source/Workspace/PlaceholderArtwork.cs#L46) extracts icons purely from `item.ExecutablePath`. As a result, every browser window displays the generic application icon (e.g., the Google Chrome or Microsoft Edge logo) and text title.
2. **Overview & Zoomed-out Ambiguity:**
   When zooming out to 10%–25% (overview mode), title text scales down or disappears, leaving a grid or cluster of indistinguishable Chrome or Edge logos. Users cannot visually locate their YouTube or GitHub windows at a glance.
3. **Performance & Concurrency Invariant:**
   Canvas relies on high-speed UI render passes and smooth 60–120 FPS panning and zooming. Any icon extraction, SQLite database queries on browser profile stores, or network I/O MUST occur asynchronously off the UI thread and cannot block `PlaceholderArtwork.UpdateItem`, `OverviewManager` passes, or WinForms message dispatch.

### Solution Goals
1. **Dual Identification Strategy:**
   - **Configured Web Resources (`WebResource` / `Target`):** Extract the web destination URL/domain directly (e.g., `youtube.com`).
   - **Live Desktop Browser Windows:** Infer the web destination from the window caption (e.g., `"Lo-Fi Beats - YouTube - Google Chrome"` -> `youtube.com` / `YouTube`) and browser profile history/mapping.
2. **Branded & Badged Visual Composite:**
   - **Primary Hero Icon (Center):** The web app's distinct high-res logo/favicon (YouTube red play icon, GitHub mark, etc.).
   - **Host Browser Sub-Badge (Bottom-Right):** A crisp, scaled-down (25%–30% size) badge of the host browser (Chrome, Edge, Firefox) with a subtle dark backing circle/border, preserving clear host identity.
3. **Asynchronous, Non-Blocking Pipeline:**
   - Initial render displays the standard browser icon immediately with zero latency.
   - Background resolution pipeline resolves the web app logo in stages:
     1. Local Canvas disk cache (`%LOCALAPPDATA%\CanvasWindowComposer\FaviconCache\`)
     2. Browser profile SQLite store (`<Profile>\Favicons`)
     3. Network favicon service fallback (e.g. Google S2 Favicon API or direct domain favicon)
   - Once resolved, the composite is placed in [PlaceholderArtworkCache](file:///Source/Workspace/PlaceholderArtworkCache.cs) and notifies active cards to invalidate and display smoothly without frame hitching.

---

## 2. Architecture & Component Design

```
+----------------------------------------------------------------------------------------------------+
|                                      PlaceholderArtwork / Form                                     |
|  - Calls PlaceholderArtworkCache.Default.GetArtwork(item)                                          |
|  - If web app logo not ready, renders browser icon immediately (Zero UI Hitch)                     |
|  - Listens to WebLogoService.ArtworkResolved -> Invalidates smoothly                               |
+----------------------------------------------------------------------------------------------------+
                                      |
                                      v
+----------------------------------------------------------------------------------------------------+
|                                     PlaceholderArtworkCache                                        |
|  - Distinct cache key: "browser:{exe}|web:{domainOrBrand}"                                         |
|  - SharedArtwork: (Composited Symbol Bitmap, Frozen WPF OverviewLogo, Bytes)                      |
|  - LRU memory budgeting (16 MB cap, GDI handle auto-disposal on eviction)                           |
+----------------------------------------------------------------------------------------------------+
                                      ^
                                      | Async Completion Dispatch
                                      |
+----------------------------------------------------------------------------------------------------+
|                                      WebLogoService (Async)                                        |
|  - Background Queue / Channel / TaskPool                                                           |
|  - Resolves webapp identity from WorkspaceItemView:                                                |
|       a) WebResource.Url / Target (Domain)                                                         |
|       b) Window Title Heuristic + Browser Caption Parser                                           |
|  - Multi-Tier Resolution:                                                                          |
|       Tier 1: Canvas Local Disk Cache (%LOCALAPPDATA%\CanvasWindowComposer\FaviconCache\)          |
|       Tier 2: Browser Profile SQLite Favicons DB (<Profile>\Favicons)                              |
|       Tier 3: Network Favicon Service (Google S2 API: https://www.google.com/s2/favicons?...)      |
|  - Badge Compositor: Draws WebApp Logo in center + Browser Badge at bottom-right                   |
+----------------------------------------------------------------------------------------------------+
```

---

## 3. Detailed Component Breakdown

### 3.1. Web App Identity & Title Parser: `WebDestinationParser.cs`
**File:** `Source/Workspace/WebDestinationParser.cs`  
*Pure, deterministic parsing without I/O.*
- **Domain Extraction:** Given a URL (from `item.WebResource.Url` or `item.Target`), extracts the normalized base domain (e.g., `youtube.com`, `github.com`, `figma.com`).
- **Title Parsing:** Given a window title from an active browser window (e.g., `Chrome`, `Edge`, `Firefox`), strips browser suffixes:
  - `" - Google Chrome"`
  - `" - Microsoft​ Edge"` / `" - Personal - Microsoft Edge"`
  - `" — Mozilla Firefox"` / `" - Mozilla Firefox"`
  - `" - Brave"`
- **Known Brand Heuristics:** Matches popular services (YouTube, GitHub, Figma, Linear, Jira, Notion, Slack, Google Docs, Google Sheets, ChatGPT, Reddit, Twitter/X, etc.) from title segments (e.g., `Title.EndsWith("- YouTube")` -> `youtube.com`).
- **Cache Key Generation:** Generates a granular key:
  `browser:chrome.exe|web:youtube.com` instead of the generic `c:\...\chrome.exe`.

### 3.2. Local & Remote Favicon Fetcher: `WebLogoStore.cs`
**File:** `Source/Workspace/WebLogoStore.cs`
- **Tier 1 (Disk Cache):** Checks `%LOCALAPPDATA%\CanvasWindowComposer\FaviconCache\{domain}.png`. Fast local file read.
- **Tier 2 (Browser Local Favicons DB):**
  - Chromium profiles store favicons in SQLite format at `<UserDataDirectory>\<Profile>\Favicons`.
  - Windows 10/11 includes `winsqlite3.dll` natively in `System32`, or Canvas can copy the DB with `FileShare.ReadWrite` to safely inspect `favicon_bitmaps` without locking conflict.
- **Tier 3 (HTTP Favicon Endpoint):**
  - Uses an asynchronous `HttpClient` to fetch `https://www.google.com/s2/favicons?domain={domain}&sz=128`.
  - Saves the resulting image to the local disk cache for instant offline reuse.
- **Resilience:** Times out within 2.5 seconds; failure gracefully falls back to the host browser icon without throwing exceptions.

### 3.3. Logo & Browser Badge Compositor: `WebLogoBadgeCompositor.cs`
**File:** `Source/Workspace/WebLogoBadgeCompositor.cs`
- Combines the 128x128 / 256x256 web app logo with the host browser icon.
- Layout:
  - Center area: High-resolution web app logo (e.g. 192x192 inside 256x256 surface).
  - Bottom-right corner: 64x64 host browser badge (Chrome, Edge, Firefox) drawn with:
    - Dark circular backing (`Color.FromArgb(220, 20, 25, 35)`) and 1.5px subtle border (`Color.FromArgb(60, 255, 255, 255)`).
    - Centered 48x48 browser icon inside the backing.
- Generates both GDI `Bitmap` (for WinForms `PlaceholderArtwork`) and frozen `BitmapSource` (for WPF `OverviewLogo`).

### 3.4. Asynchronous Coordinator: `WebLogoService.cs`
**File:** `Source/Workspace/WebLogoService.cs`
- Singleton service managing in-flight logo resolution.
- Thread-safe deduplication (avoids multiple concurrent fetches for the same domain).
- Dispatches resolution onto `Task.Run` / background thread.
- On completion:
  1. Stores the composite `SharedArtwork` into `PlaceholderArtworkCache.Default`.
  2. Raises event `WebLogoService.Default.ArtworkResolved(string cacheKey)`.
- Active `PlaceholderArtwork` controls matching the cache key invoke `Invalidate()` on their UI thread to display the new artwork smoothly.

### 3.5. Integration into `PlaceholderArtworkCache` & `PlaceholderArtwork`
- **Cache Key Update:**
  In `PlaceholderArtworkCache.GetCacheKey(WorkspaceItemView item)`:
  If `BrowserSupport.Supports(item.ExecutablePath)` and `WebDestinationParser.TryGetDestination(item, out var dest)` is true:
  Key becomes `$"browser:{Path.GetFileName(item.ExecutablePath).ToLowerInvariant()}|web:{dest.ToLowerInvariant()}"`.
- **GetArtwork Flow:**
  - If cached, return immediately.
  - If not cached:
    - Return default browser artwork immediately (zero UI hitch).
    - Queue asynchronous background resolution via `WebLogoService`.
- **On ArtworkResolved:**
  `PlaceholderArtwork` receives the update, switches `_symbol` and `_overviewLogo`, and invalidates.

---

## 4. Atomic Task Breakdown

- [ ] **Task 1: WebDestinationParser & Title Heuristics**
  - Implement `Source/Workspace/WebDestinationParser.cs`.
  - Parse URLs and clean browser titles to identify domains / web app brands.
  - Unit tests in `Tests/WebDestinationParserTests.cs` (coverage for Chrome, Edge, Firefox titles, URLs, and edge cases).

- [ ] **Task 2: Badge Compositing Engine**
  - Implement `Source/Workspace/WebLogoBadgeCompositor.cs`.
  - Composite web app icon + browser corner badge with GDI anti-aliasing.
  - Unit tests in `Tests/WebLogoBadgeCompositorTests.cs` (verifying dimensions, alpha channel, and non-null overview bitmap).

- [ ] **Task 3: Local Cache & Fetcher Pipeline**
  - Implement `Source/Workspace/WebLogoStore.cs`.
  - Local disk cache management in `%LOCALAPPDATA%\CanvasWindowComposer\FaviconCache\`.
  - Safe local browser profile SQLite / file inspection and HTTP favicon fetcher with timeout and offline protection.
  - Unit tests in `Tests/WebLogoStoreTests.cs` with mocked HTTP/filesystem.

- [ ] **Task 4: Asynchronous WebLogoService & Cache Integration**
  - Implement `Source/Workspace/WebLogoService.cs`.
  - Update `PlaceholderArtworkCache.cs` to incorporate domain-specific keys.
  - Update `PlaceholderArtwork.cs` to subscribe to `ArtworkResolved` and refresh cleanly.
  - Unit tests in `Tests/WebLogoServiceTests.cs` (verifying async resolution, zero UI deadlock, and cache deduplication).

- [ ] **Task 5: End-to-End Verification**
  - Build solution with locked mode.
  - Execute test suite via `Tools/Run-Tests.ps1 -Configuration Release`.
  - Verify cards update in both WinForms detail view and WPF zoomed-out overview.

---

## 5. Verification Strategy & Invariants

1. **Non-Native Unit Tests:**
   All components are decoupled from live HWNDs and run under standard `dotnet test` or Phase 1 of `Run-Tests.ps1`:
   - Fast, in-memory, deterministic execution.
2. **UI Thread & Concurrency Invariant:**
   - No HTTP requests, SQLite file reads, or disk I/O on the UI thread.
   - All GDI bitmaps allocated in background tasks are transferred safely, and WPF `BitmapSource` objects are strictly `.Freeze()`d before crossing thread boundaries.
3. **Memory & GDI Handle Budget:**
   - Memory continues to be bounded by `PlaceholderArtworkCache`'s 16 MB LRU cap.
   - Eviction properly disposes underlying GDI `Bitmap` handles.
