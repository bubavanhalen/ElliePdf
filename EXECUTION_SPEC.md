# ElliePdf Production Execution Specification

- Status: Execution baseline v1.0; ready for backlog decomposition
- Target: Production-quality Windows PDF reader
- Primary platform: WinUI 3, .NET 11 preview, newest compatible preview toolchain
- Planning assumption: Four core Windows/C# engineers, dedicated QA from Sprint 1, and part-time product design and security support
- Estimated calendar duration: 24–28 weeks. With two core engineers, plan for 36–42 weeks and keep the same gates.

## 1. Objective

Turn ElliePdf from a functional alpha into one of the fastest, most elegant PDF readers on Windows while preserving its native WinUI direction.

The work is reader-first. A production release must excel at opening, displaying, navigating, searching, selecting and printing PDFs before advanced editing and organization are promoted from preview status.

Success is defined by measured performance, zero-loss saving, accessibility, native-parser isolation and repeatable signed releases. Feature count is not a success metric.

### 1.1 Audit-to-execution traceability

| Current blocker | Owning work |
|---|---|
| In-place/truncating save risk and ambiguous discard/recovery state | WP-02 |
| Native handle identity/lifetime hazards and obsolete PDFium supply | WP-03, WP-13A |
| Eager all-page controls/renders in continuous mode | WP-05, WP-06 |
| Global PDFium contention and whole-document search monopolization | WP-06, WP-09 |
| PNG encode/decode in the display path and unbounded page rasters | WP-07, WP-08 |
| Raster-only pages without selection, links or accessibility semantics | WP-09, WP-11 |
| x64-only runtime identity despite ARM64 intent | WP-00, WP-03, WP-14 |
| Parser runs in the UI trust boundary | WP-13A, WP-13B |
| Incomplete packaging, release, privacy and performance evidence | WP-01, WP-14 |

## 2. Fixed decisions and constraints

1. ElliePdf remains a native WinUI 3 application.
2. ElliePdf remains on .NET 11 preview and tracks the newest compatible preview toolchain.
3. PDFium remains the initial PDF engine.
4. The UI must remain responsive regardless of document page count.
5. Untrusted PDFs must ultimately be parsed outside the UI process.
6. Original PDFs must never be damaged by an interrupted or failed save.
7. x64 and ARM64 are first-class release architectures. Every advertised architecture must pass the complete signed release matrix.
8. All user-visible features must support keyboard operation, system text scaling, High Contrast and Narrator.
9. Existing in-progress reader changes are preserved and migrated incrementally. This is not a big-bang rewrite.
10. Microsoft Store is the production v1 distribution vehicle. Signed MSIX canaries may be side-loaded internally; AppInstaller distribution is post-v1.

### 2.1 Preview toolchain policy

Using previews is an explicit product decision. The following controls make that decision operationally manageable:

- Pin exact SDK and package versions. Floating versions are prohibited.
- Pin the exact .NET 11 SDK in `global.json` with `allowPrerelease: true` and `rollForward: disable`. Only the preview-update pipeline may edit that version.
- Centralize top-level package versions in `Directory.Packages.props`.
- Commit NuGet lock files and use locked restore in CI and release builds.
- Track the newest public .NET 11 preview or release candidate. Evaluate new builds within two business days and promote them within five business days after every mandatory gate passes without suppressing project errors.
- Track the newest compatible public preview of every direct dependency that publishes a supported Preview channel. If a package family has no preview, retain its newest compatible release until one exists.
- Do not downgrade the SDK or a dependency solely to move ElliePdf onto a stable support channel.
- Treat Experimental channels as distinct from Preview. Experimental packages require a short architecture decision record identifying the required feature and rollback plan.
- Run a scheduled preview-update pipeline at least weekly. It must restore, compile, publish Native AOT, package, run tests and execute the performance smoke suite.
- Maintain the last-known-good source, locks and unsigned package payload for forward rollback. A failed preview update blocks promotion; it does not silently move the main branch to a stable .NET release.
- Every artifact must record SDK, runtime, Windows App SDK, Windows SDK, PDFium and package-lock fingerprints.
- Known upstream preview warnings may be allowlisted only with an issue URL, owner and expiry date. Project warnings are errors.

## 3. Production v1 scope

### 3.1 Required reader capabilities

- Fast file activation, open and first-page presentation.
- Continuous and single-page modes with smooth scrolling and anchor-preserving zoom.
- Multiple tabs with tab switching, dirty state and session restoration.
- Page navigation, editable page number, thumbnails and semantic outline tree.
- Progressive search with result snippets, next/previous navigation and cancellation.
- Text selection and copy.
- Internal and external links with safe activation behavior.
- AcroForm viewing, filling and atomic persistence for text, checkbox, radio, combo/list and safe push-button widgets.
- Password-protected PDFs, printing and document properties.
- Mouse, keyboard, precision-touchpad, touch and pen parity.
- Light, dark and High Contrast themes.
- Narrator semantics, system text scaling and localization infrastructure.

### 3.2 Preview-only capabilities until their gates pass

- Page organization and merging.
- Ink, text and image-signature stamps.
- PDF annotation writing.

These capabilities ship under a **Labs** switch that is off by default and clearly labeled. Labs features still use the production atomic-save path and may not bypass security, integrity or telemetry rules.

XFA, signature widgets and widgets with unsafe actions are read-only and expose an accessible unsupported notice.

The existing image-based signature feature must be called a **signature stamp**. It must not be presented as a cryptographic digital signature.

### 3.3 Out of scope for production v1

- Cloud accounts or document synchronization.
- OCR as a required dependency. OCR may later ship as an optional component.
- Cryptographic signing and certificate management.
- Real-time collaboration.
- Editing arbitrary existing PDF text and vector content.
- PDF JavaScript, XFA, multimedia, arbitrary launch actions and embedded-executable extraction.

## 4. Measurable release objectives

Measurements use a controlled reference laptop with at least four modern CPU cores, 16 GB RAM, NVMe storage, integrated graphics and Windows 11. Tests run at 100%, 150% and 200% display scaling.

| Scenario | Production gate |
|---|---:|
| Cold launch to interactive shell | p95 ≤ 600 ms |
| Warm file activation to readable first page | p95 ≤ 300 ms |
| Cold activation of a local 10–20 MB PDF | p95 ≤ 800 ms |
| Cached page navigation | p95 ≤ 50 ms |
| Uncached viewport-quality tile or page | p95 ≤ 200 ms |
| Random jump to cached low-resolution preview | p95 ≤ 80 ms |
| Random jump to uncached low-resolution preview | p95 ≤ 200 ms |
| Random jump to sharp result | p95 ≤ 300 ms |
| Zoom or pan visual response | Transform submitted for the next composition commit; input-to-present p95 ≤ 2 refresh intervals |
| Sharp tiles after zoom settles | p95 ≤ 200 ms |
| Continuous scrolling | p95 frame ≤ 16.7 ms; p99 ≤ 33 ms; < 1% dropped frames |
| Stale queued work cancellation | Old-generation work becomes ineligible for publication p95 ≤ 10 ms |
| Active progressive-render cancellation yield | ≤ 25 ms where the native API is progressive |
| 1,000-page steady-state memory | Aggregate UI + worker private committed bytes ≤ 300 MiB; GPU allocation ≤ 96 MiB |
| Document close memory release | Aggregate private committed bytes return to within 10% of the pre-open baseline within 2 seconds |
| 10,000-page first page | Cold p95 ≤ 1 second; realized controls, subscriptions and raster surfaces are bounded independently of page count |
| Idle utilization | < 0.5% CPU and no recurring disk writes |
| Save integrity | Zero damaged originals across 10,000 fault-injected saves |
| Reliability | ≥ 99.9% crash-free and ≥ 99.95% hang-free sessions |
| Accessibility | Zero critical/high findings and all primary workflows keyboard/Narrator-complete |

These are target SLOs, not claims about the current application. ElliePdf may be called “one of the fastest” only after the same corpus and cold/warm procedure show competitive results against three leading Windows readers.

Benchmark terms are normative:

- **Cold open** means a new process after the harness clears ElliePdf's application caches and flushes the test file from the standby list using the documented administrator-controlled procedure. **Warm activation** means an already-interactive shell with OS file cache retained, the document currently closed and its in-memory render cache empty unless the scenario explicitly says cached.
- **Readable** means the first viewport has presented a non-placeholder image at a scale at which body text in the fixture is legible. **Sharp** means all visible tiles at the target scale have presented.
- A frame is dropped when a composition presentation misses its refresh deadline; the scroll test reports input-to-present intervals over a fixed 30-second trace after five seconds of warm-up.
- Latency tests run at least 30 measured iterations per fixture and report median, p95, p99 and a bootstrap 95% confidence interval. A gate is stable only when the p95 confidence-interval width is at most 10% of the estimate.
- Shared mappings are counted once in process-tree memory accounting. The report lists UI private committed bytes, worker private committed bytes, shared mappings, GPU allocation and each cache budget separately.
- WP-01 compares Adobe Acrobat Reader, Microsoft Edge's PDF viewer and SumatraPDF, freezing their exact versions, settings, corpus hashes, power mode, cache-clearing procedure and statistical calculation before optimization starts. A speed claim requires ElliePdf to meet every reliability/memory gate and finish within 10% of the best comparator for launch, first page, steady scroll and random jump.

## 5. Target architecture

```text
ElliePdf.WinUI
  Views, adaptive shell, input, accessibility, composition surfaces
                         │ commands and immutable snapshots
ElliePdf.Application
  DocumentWorkspace, use cases, state machines, undo/redo
                         │ ports
ElliePdf.Domain
  Document identity, revisions, page plan, view state, edit operations
           ┌─────────────┴─────────────┐
ElliePdf.Rendering              ElliePdf.Infrastructure
  viewport scheduling             atomic storage and recovery
  tile/cache policies              settings, activation, diagnostics
           │ IPdfEngineClient
ElliePdf.Pdf.Contracts
           │ authenticated IPC
ElliePdf.Pdfium.Worker
  restricted parser/render process, engine lane, shared-memory transfer
           └─ references ElliePdf.Pdfium (ABI and native lifetime only)
```

### 5.1 Planned solution layout

```text
src/ElliePdf.WinUI
src/ElliePdf.Application
src/ElliePdf.Domain
src/ElliePdf.Rendering
src/ElliePdf.Infrastructure
src/ElliePdf.Pdf.Contracts
src/ElliePdf.Pdfium
src/ElliePdf.Pdfium.Worker
tests/ElliePdf.Domain.Tests
tests/ElliePdf.Application.Tests
tests/ElliePdf.Rendering.Tests
tests/ElliePdf.Pdfium.IntegrationTests
tests/ElliePdf.UI.AutomationTests
tests/ElliePdf.PerformanceTests
tests/ElliePdf.PackagingTests
testdata/manifest.json
```

Migration is strangler-style:

1. Create projects and contracts without moving working UI code.
2. Wrap the current `PdfService` behind the new engine contract.
3. Move one responsibility and its tests at a time.
4. Switch callers through dependency injection.
5. Delete legacy behavior only after functional and performance parity is demonstrated.

### 5.2 Required dependency rules

- `Domain` references no Windows, WinUI, PDFium or storage packages.
- `Application` references `Domain` and abstractions only.
- `Rendering` contains scheduling and cache policy but no dialogs or file pickers.
- `Pdfium` is the only project permitted to contain PDFium P/Invoke declarations.
- Only `Pdfium.Worker` and PDFium integration tests may reference `Pdfium`; WinUI, Application, Domain and Rendering reference transport-neutral Contracts/client abstractions only.
- `WinUI` owns controls, dialogs, pickers, drag/drop and UI Automation peers.
- Static service location and static navigation events are removed.
- Workspace state is instance-scoped and every tab owns one `DocumentContext`. Production v1 hosts one visible workspace; a second workspace must be constructible in a headless test without static tab/view state.

## 6. Core contracts

The following shapes are normative design contracts. Exact syntax may change, but their identity and lifecycle semantics may not.

### 6.1 Document identity and revisions

```csharp
public readonly record struct DocumentId(Guid Value);
public readonly record struct PageId(Guid Value);
public readonly record struct ContentRevision(long Value);
public readonly record struct StructureRevision(long Value);
public readonly record struct PageContentRevision(long Value);
public readonly record struct PageAppearanceRevision(long Value);
public readonly record struct RenderGeneration(long Value);
public readonly record struct SearchGeneration(long Value);

public sealed record DocumentSnapshot(
    DocumentId Id,
    ContentRevision ContentRevision,
    ContentRevision SavedRevision,
    StructureRevision StructureRevision,
    string DisplayName,
    int PageCount,
    int CurrentPageIndex,
    bool HasUnsavedChanges,
    RecoveryState RecoveryState,
    ExternalFileState ExternalFileState);

public sealed record PageSnapshot(
    PageId Id,
    int PageIndex,
    PageContentRevision ContentRevision,
    PageAppearanceRevision AppearanceRevision,
    PdfSize SizeInPoints);
```

- `DocumentId` is allocated by ElliePdf and never derives from a native pointer.
- `PageId` is stable while a page is reordered. `StructureRevision` changes when membership or order changes.
- Global `ContentRevision` increments only for saveable PDF or edit-plan changes. Every form edit and persistent page/content operation participates in this revision, undo, recovery and close prompting.
- Per-page content/appearance revisions invalidate only affected page artifacts. A persistent page change also increments global `ContentRevision`.
- `RenderGeneration` changes for content, zoom, DPI, theme, view rotation or tile-policy changes. `SearchGeneration` changes for query or search-option changes.
- Navigation, zoom, DPI, sidebar state and search queries never increment `ContentRevision` and never make the document dirty.
- Every asynchronous result carries its document/page identity, content identity and generation. Mismatched results are ineligible for publication.
- Native handles are never cache keys and never cross the PDFium boundary as application state.

### 6.2 PDF engine contract

```csharp
public interface IPdfEngineSession : IAsyncDisposable
{
    DocumentId DocumentId { get; }
    ValueTask<PdfMetadata> GetMetadataAsync(CancellationToken cancellationToken);
    ValueTask<PageMetadata> GetPageMetadataAsync(int pageIndex, CancellationToken cancellationToken);
    ValueTask<IPixelBufferLease> RenderAsync(RenderRequest request, CancellationToken cancellationToken);
    ValueTask<PageTextResult> GetPageTextAsync(PageTextRequest request, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<SearchResult>> SearchPageAsync(
        PageSearchRequest request,
        CancellationToken cancellationToken);
}
```

- All PDFium calls execute on one owned engine lane per worker because PDFium APIs are not thread-safe.
- Visible render jobs have priority over prefetch, thumbnails and indexing.
- Search and thumbnail work is page-scoped at the low-level contract. An Application coordinator schedules pages and streams aggregate results without monopolizing the engine lane.
- Encoding, pixel upload and non-PDFium processing happen outside the engine lane.
- PDFium resources are deterministically destroyed on the engine lane. Finalizers may report leaks but must not call PDFium; worker termination is the ultimate cleanup boundary.
- Metadata, outline, text geometry, link, form, permission and print DTOs are transport-neutral, versioned and bounded before semantic UI work begins.

### 6.3 Render request and result identity

```csharp
public readonly record struct RasterScale64(int Value);

public readonly record struct TileAddress(
    int X,
    int Y,
    int InteriorWidth,
    int InteriorHeight,
    int BleedPixels);

public sealed record RenderKey(
    DocumentId DocumentId,
    PageId PageId,
    PageContentRevision ContentRevision,
    PageAppearanceRevision AppearanceRevision,
    TileAddress Tile,
    RasterScale64 RasterScale,
    PageRotation Rotation,
    RenderMode Mode);

public sealed record RenderRequest(
    RenderKey Key,
    RenderGeneration Generation,
    RenderQuality Quality,
    EngineJobPriority Priority,
    DateTimeOffset Deadline);

public interface IPixelBufferLease : IAsyncDisposable
{
    Guid LeaseId { get; }
    string SharedMemoryId { get; }
    long Offset { get; }
    int ByteLength { get; }
    int Width { get; }
    int Height { get; }
    int Stride { get; }
    PixelFormat Format { get; }
    RenderKey Key { get; }
}
```

- `RasterScale64` is physical pixels per PDF point, ceiling-quantized to 1/64. `TileAddress` is expressed in page-raster physical pixels; tiles use a 512×512 interior plus one-pixel bleed on available edges.
- `PixelFormat` is BGRA8 premultiplied alpha for v1. The IPC lease protocol defines acquire, acknowledgement, release and timeout/reclaim behavior.
- `RenderKey` contains every fact that affects pixels. Generation, priority and deadline are scheduling facts and are deliberately excluded from cache identity.
- Every successful completion, cancellation, stale rejection, tab close and worker crash releases a buffer lease exactly once. Stale pixels are never uploaded.

### 6.4 Save and recovery state

Dirty state is modeled using independent facts rather than one ambiguous Boolean:

- `HasUnsavedChanges`: derived from `ContentRevision != SavedRevision`; it has no public setter.
- `RecoveryState`: `None`, `Pending`, `Checkpointed`, or `Failed`.
- `CommitState`: `Idle`, `Saving`, or `Failed`.
- `ExternalFileState`: `Unchanged`, `Changed`, `Missing`, or `Unknown`.

`FileVersionStamp` contains the canonical path, volume/file identity, length and last-write UTC, plus a content hash when identity is unavailable or metadata differs. `FileSystemWatcher` is advisory only; the stamp is reread immediately before commit.

Rules:

- A recovery checkpoint never marks the PDF clean.
- Closing prompts whenever `HasUnsavedChanges` is true, regardless of recovery state.
- Discard never saves/checkpoints and never modifies the source or destination PDF; it deletes existing recovery artifacts and closes.
- Save acquires a canonical per-destination lock and verifies the expected `FileVersionStamp`.
- Save creates a `CreateNew` temporary file in the destination directory and gives the worker only a duplicated writable temporary handle.
- The worker serializes the captured content revision, then the coordinator durably flushes/closes the temp file and validates it in a fresh engine session.
- Commit is non-cancellable and uses atomic replace/move. If the filesystem cannot prove this operation, ElliePdf fails safely and offers Save As; it never falls back to truncation, delete-then-move or copy-overwrite.
- After commit, ElliePdf reopens/fingerprints the destination and marks only the captured revision saved. If a newer edit exists, the document remains dirty.
- A recoverable backup and journal remain until post-commit validation completes. Network/cloud destinations without proven atomic replacement are Save As only.
- External file fingerprint changes require an explicit conflict decision.
- Recovery data is stored atomically in protected application data by default, not beside the source PDF.
- Paths, passwords, search text, annotations and signature-stamp image data are excluded from telemetry.

## 7. Execution work packages

Each work package becomes an Epic. An implementation issue is ready only when it names the affected contract, fixture, telemetry event and acceptance test. It is done only when code, automated tests, relevant manual script, diagnostics and documentation are merged; x64/ARM64 and Native AOT gates pass; and performance/golden baselines change only through an explicitly reviewed baseline update.

The four core engineering streams are: integrity/application state, PDFium/worker/security, rendering/performance, and WinUI/semantics. QA owns corpus automation, release-matrix evidence and fault injection; product design owns interaction specs and visual/accessibility review. Cross-stream contracts in Section 6 require approval from both consuming streams.

### WP-00 — Repository and preview governance

Deliverables:

- `global.json`, central package management and NuGet lock files.
- Preview-update workflow and last-known-good version manifest.
- Build metadata containing the complete toolchain fingerprint.
- Warning policy with expiring upstream-preview allowlist.
- Architecture decision records for Native AOT, Windows App SDK channel and any Experimental dependencies.
- `SUPPORTED_WINDOWS.md` naming one minimum supported Windows build.
- An ADR recording Microsoft Store as the v1 GA vehicle and the non-GA status of AppInstaller distribution.

Acceptance criteria:

- A clean checkout restores deterministically with locked mode.
- x64 and ARM64 builds use architecture-compatible runtime identifiers.
- Native AOT publish is exercised in CI from the first sprint.
- A preview SDK update can be validated and rolled back without editing application code.
- The csproj target/minimum, both manifest families, package metadata, README and blocking device matrix agree on the Windows floor.
- The supported minimum and current Windows GA builds block release; the latest Insider build is a non-blocking compatibility signal.

### WP-01 — Benchmark and test corpus

Deliverables:

- Licensed/hash-pinned corpus covering small vector PDFs, photo scans, CJK/font-heavy files, mixed sizes/orientations, links, forms, outlines, encryption, 1,000 and 10,000 pages, 1 GB files, huge MediaBoxes, corrupt inputs and known parser-stress cases.
- ETW/EventSource events for activation, open, metadata, render queue wait, native render, pixel upload, first page presented, cache hit/eviction, search, save stages and worker failures.
- Automated cold/warm benchmark harness and report template.
- Frozen comparator manifest for Adobe Acrobat Reader, Microsoft Edge's PDF viewer and SumatraPDF, naming exact versions, settings, corpus hashes, cache procedure, device/power configuration, repetitions and statistical method.

Acceptance criteria:

- A benchmark run satisfies the confidence-interval rule in Section 4 on the reference machine.
- Results include CPU, working set, allocation rate, frame time, queue latency and cache bytes.
- Performance results never contain document names, paths or extracted content.

### WP-02 — Data-integrity foundation

Deliverables:

- `IAtomicDocumentStore` and transactional save implementation.
- Explicit document dirty/recovery/commit/external-conflict state machine.
- Atomic recovery checkpoints with schema versioning and corruption handling.
- Correct save/discard/cancel behavior shared by tab close, window close, edit and Organizer flows.
- Immediate disabling of existing destructive Organizer/annotation-save commands unless Labs is on, until the immutable page plan exists.

Acceptance criteria:

- Fault injection at every save stage never damages the original.
- Saving over the open source uses temporary output and atomic replacement.
- Cancellation before commit leaves the destination unchanged; cancellation is ignored after non-cancellable commit begins.
- Random worker/UI termination at every transaction stage yields either the complete old file or complete new file.
- Saving captured revision `r` while revision `r+1` is created commits `r` and leaves the document dirty.
- Concurrent saves to one canonical destination serialize; an unexpected version stamp produces a conflict instead of overwrite.
- A recovery checkpoint never changes `SavedRevision`.
- Default Save round-trips selectable text, links, outlines, forms, metadata, permissions and page geometry. Raster flattening is available only as an explicit Save Copy operation.
- Discard produces no source/destination PDF writes and removes existing recovery artifacts.
- Read-only, locked, missing, network and externally modified files produce deterministic user choices.
- Crash restart offers valid recovery exactly when uncommitted user work exists.

### WP-03 — Native dependency and lifetime safety

Deliverables:

- Current, pinned x64 and ARM64 PDFium binaries from a controlled source.
- Hash verification, provenance record, license/third-party notice and SBOM inclusion.
- App-private DLL resolution only; current-directory fallback is removed.
- Explicit deterministic ownership wrappers for every native resource; wrappers prevent double-close and do not invoke PDFium from finalizers.
- Pixel and page-dimension limits using checked 64-bit arithmetic.

Acceptance criteria:

- The application refuses an unexpected PDFium hash or architecture.
- No raw owning `IntPtr` remains outside the native adapter.
- Native resources are disposed deterministically on the worker engine lane; a test proves no PDFium close/destroy call runs on a finalizer or other lane.
- Corrupt, huge and memory-pressure documents cannot allocate outside configured budgets.
- Closing a document releases native handles and at least 90% of associated memory within two seconds.

### WP-04 — Document workspace and application core

Deliverables:

- Per-window `DocumentWorkspace` and per-tab `DocumentContext`.
- Immutable UI snapshots and command-based mutations.
- Explicit cancellation lifetime per document and per render/search generation.
- Dialog, file-picker, navigation and notification ports owned by the UI layer.
- Background-task supervisor that observes every fire-and-forget operation.
- Transport-neutral DTOs for metadata, outline, page/text geometry, links, forms, permissions and print requests.

Acceptance criteria:

- Tabs can open, activate and close under headless application tests.
- No application/domain type references `BitmapImage`, `ContentDialog`, `InfoBarSeverity`, `XamlRoot` or a global window.
- Closing a context cancels and observes all owned operations before disposing its session.
- A second workspace can be constructed and tested headlessly without shared tab/view state; user-visible multi-window is post-v1.
- Later file activations are serialized into the existing window, and opening an already-open file activates its tab.

### WP-13A — Worker protocol and engine boundary

This work package precedes the scheduler and renderer. All production PDFium calls cross this boundary before WP-06 begins, avoiding a later in-process-to-IPC rewrite.

Deliverables:

- Versioned, length-prefixed and size-bounded authenticated IPC protocol.
- Brokered read-only source handles and transaction-scoped writable temporary handles; paths are never sent as worker authority.
- One worker per app instance under a Job Object, one owned engine lane and the authoritative priority queue beside that lane.
- Shared-memory `IPixelBufferLease` transport with acknowledgement, exactly-once release, timeout reclamation and worker-crash cleanup.
- Worker launch, heartbeat, operation deadlines, crash detection, orphan cleanup and unaffected-tab recovery.

Acceptance criteria:

- WinUI/Application/Domain/Rendering cannot reference or load PDFium directly.
- IPC rejects missing/incorrect per-launch secrets, stale identities, invalid frame lengths, oversized arrays and unsupported protocol versions.
- Source handles never gain write access; temporary write authority ends when the transaction stage closes.
- Killing either endpoint at every lease transition leaves no retained mapping or native handle.
- After three crashes attributed to one document within five minutes, it is quarantined for the session and reopened only after explicit user action; unaffected tabs recover and protected files request passwords again only when activated.

### WP-05 — Virtualized page host

Deliverables:

- Replace continuous `ItemsControl + StackPanel` with `ItemsRepeater` and a virtualizing variable-height layout.
- Maintain lightweight metadata for all pages; realize UI only for the viewport plus configurable overscan.
- Maintain estimated/exact page extents in an indexed prefix-sum structure so geometry updates and offset-to-page lookup remain `O(log n)`.
- Determine current page from layout offsets/visible range without scanning every page container.
- Element prepare/clear events start and cancel viewport render requests.

Acceptance criteria:

- A 10,000-page document does not create page controls, subscriptions, automation peers or bitmap surfaces proportional to page count.
- On the frozen 10,000-page portrait fixture, realized page controls remain bounded to viewport plus overscan and never exceed 12; page-scoped subscriptions exist only for realized elements and raster leases obey WP-07's two-lease cap.
- Scrolling work is proportional to realized pages, not total pages.
- Mixed page sizes and rotations preserve correct offsets and page navigation.
- Recycled elements never display pixels or automation metadata from another page.
- A 10,000-page file does not create 10,000 automation peers. Narrator can request next/previous pages through scrolling; recycled peers update name, position, text range and bounds atomically, and keyboard focus survives recycling.

### WP-06 — Priority render scheduler

Required engine-job priority order:

1. Visible interaction-critical work, including a search target after navigation makes it current.
2. Other visible render, text, link and form work.
3. Immediate directional overscan.
4. Visible thumbnails and UI-requested metadata.
5. Directional prefetch.
6. Background search/indexing and non-visible thumbnails.

Deliverables:

- Authoritative priority queue beside the worker engine lane, request deduplication and fair scheduling between tabs.
- Latest-generation-wins result publication.
- Page-sized search and thumbnail jobs.
- Progressive-render pause/cancel support where available.
- Reference-counted busy state rather than competing Boolean flags.
- Global pending capacity of 256 jobs, a default per-document pending quota of 64 and single-flight deduplication by job identity. Visible work is never dropped; backpressure evicts oldest background indexing, then non-visible thumbnails, then farthest prefetch.

Acceptance criteria:

- Background search of a 10,000-page document does not delay a visible render beyond its SLO.
- Duplicate tile requests share one in-flight operation.
- Incrementing a generation makes old queued work ineligible for publication within 10 ms p95; physical queue removal may be lazy.
- Active progressive work yields to cancellation within 25 ms. A non-progressive native call may finish at most the currently executing bounded tile, but its result is suppressed; p99 native tile-call duration must remain within its render deadline and a watchdog handles overruns.
- A tab cannot publish render/search results after it closes or changes revision.

### WP-07 — Direct-pixel tiled renderer

Deliverables:

- Time-boxed benchmark spike comparing direct `WriteableBitmap`/`SoftwareBitmap` upload with a Win2D/Direct2D composition surface.
- Worker rendering into broker-pooled BGRA shared-memory leases.
- Normative 512×512 physical-pixel tile interiors with one-pixel bleed, clipped PDFium rendering and direct upload without PNG encoding/decoding.
- Separate CPU-buffer and GPU-surface caches with byte budgets and memory-pressure eviction.
- Global initial cache budgets are 96 MiB GPU tiles, 32 MiB CPU handoff buffers, 16 MiB thumbnails and 16 MiB geometry/text metadata. A central manager may lower them under pressure.
- Tile rendering is mandatory when either page-raster dimension exceeds 2,048 pixels or a full-page BGRA buffer would exceed 16 MiB.
- Cache identity is exactly the `RenderKey` contract; scheduling generation/priority never fragment the cache.

Acceptance criteria:

- No PNG codec is used for on-screen page rendering.
- No full-page allocation is required at high zoom.
- Resident cache bytes never exceed configured budgets. At most two uncached tile leases and 8 MiB total may be in flight above the resident budgets.
- Rotation, deletion, edits, DPI changes and theme transforms cannot return stale pixels.
- Golden output has exact dimensions and no tile seams; per-architecture baselines require SSIM ≥ 0.995 and fewer than 0.5% of pixels with any channel delta above 8.

### WP-08 — Composition zoom, DPI and prefetch

Deliverables:

- Immediate compositor transform around the pointer/touch focal point.
- Scale-bucketed sharp tile replacement after input settles.
- Geometry uses `pointsToDips = zoom × 96 / 72`; rasterization uses `pointsToPhysicalPixels = pointsToDips × XamlRoot.RasterizationScale`, ceiling-quantized to `RasterScale64`.
- Directional prefetch based on scroll velocity.
- Low-resolution placeholder for random jumps followed by sharp replacement.
- Checked zoom range from 10% to 6,400% without full-page allocation or arithmetic overflow.

Acceptance criteria:

- Zoom/pan submits a composition transform for the next commit and meets the Section 4 input-to-present gate.
- Moving the window between monitors rerenders to correct physical resolution without changing logical zoom.
- Rapid zoom never clears the document to an empty state.
- Prefetch never evicts visible tiles or starves direct user work.

### WP-09 — Search and semantic document layer

Deliverables:

- Incremental per-page text extraction with geometry and reading-order metadata.
- Streaming search results containing page, context and highlight geometry.
- Text selection, keyboard selection and clipboard copy.
- Internal destinations, URI links and interactive form overlays.
- Outline tree and non-blocking document-properties projection.
- AcroForm value model for text, checkbox, radio, combo/list and safe push-button widgets; changes increment `ContentRevision` and participate in undo/recovery/close/save.
- Custom page/document automation peers exposing meaningful names, reading order, text and link/form patterns where supported.

Acceptance criteria:

- First search result is displayed before full-document scanning completes.
- F3/Shift+F3 navigation and cancellation work during indexing.
- Selected text matches PDF text order on the corpus, including Unicode and rotated pages.
- Links are keyboard focusable; external URI activation follows an explicit safe-link policy.
- Only `https`, `http` and `mailto` URI schemes may leave the app after explicit activation. JavaScript, `file:`, shell/launch actions and embedded executables are blocked.
- Text copying, printing, form updates and structural edits honor the document permission flags exposed by PDFium.
- Narrator can navigate pages and readable text without announcing the page as only an image.
- Form values survive save/reopen; untouched fields, appearances, tab order and permissions round-trip across the corpus. XFA, signature and unsafe-action widgets remain read-only with an accessible notice.
- Outline hierarchy, titles, destinations and keyboard expansion match the corpus, including safe handling of malformed destinations.
- Properties displays title, author, subject, creator, PDF version, page count, page size, encryption and permissions without delaying first-page presentation.

### WP-10 — Adaptive Windows reader experience

Deliverables:

- Responsive `CommandBar` with primary commands and overflow instead of a fixed horizontal toolbar.
- Unified sidebar modes for thumbnails, outline and search results.
- Dynamic title-bar drag regions that never overlap tabs or interactive controls.
- Editable page-number control, dirty markers, cancellable progress and focus mode.
- Session restoration for tabs, page, zoom, view mode, window bounds and sidebar state.
- Complete keyboard accelerator map, drag/drop open, precision-touchpad pinch and touch gestures.
- Worker-backed print preview and bounded spool rendering for current/all/custom ranges, fit/actual size, auto-orientation, permission enforcement and cancellation.
- Responsive breakpoints: docked 280–360 epx sidebar at widths ≥1,000; 320 epx overlay sidebar from 640–999; full-height overlay below 640.
- Minimum supported client area of 500×320 effective pixels.

Acceptance criteria:

- At 500×320 and 400% text, all primary operations remain reachable through overflow or a scrollable pane; controls do not overlap caption buttons/page content, focused items are not clipped and opening chrome preserves the page anchor.
- Every command is reachable with keyboard alone and exposes a name, state and shortcut.
- Ctrl+S, Ctrl+Shift+S, Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+G, Page Up/Down, Home/End, Space/Shift+Space, F3/Shift+F3 and Escape behave consistently.
- F6 cycles tabs, commands, sidebar, document and status; F11 enters/exits focus mode.
- Restoring an invalid/missing file does not block restoration of other tabs.
- The title bar, tabs and caption buttons pass hit-testing at all supported scales.
- Print jobs with one page, mixed orientation and 1,000 pages produce the correct count/order and golden output. At most three page surfaces or 128 MiB are retained; cancellation yields within one second; denied printing is disabled with an explanation.
- `Ctrl+wheel` and pinch preserve the pointer/centroid anchor; wheel, touchpad and touch retain inertia; touch selection handles work; links/forms have 44 epx targets; pen cannot ink outside Labs edit mode; double-click selects a word and triple-click a visual line.
- Passwords and extracted content are never persisted. Locked tabs restore as locked placeholders and prompt only when activated. Users can disable reopen/history and separately clear recents, view state, diagnostics and recovery data; deleting recovery requires a data-loss warning.

Normative shortcut behavior:

| Shortcut | Behavior |
|---|---|
| `Ctrl+Home` / `Ctrl+End` | First / last page |
| `Home` / `End` | Start / end of the current scroll region |
| `PageUp` / `PageDown` | One viewport backward / forward |
| `F3` / `Shift+F3` | Next / previous search result |
| `Ctrl+C` | Copy only an active text selection |
| `Ctrl+S` | Save; enabled only for a saveable dirty document |
| `Ctrl+Shift+S` | Save As |
| `Esc` | Close only the topmost transient surface and restore invoking focus |

Text and form controls retain standard editing shortcuts while they own focus.

### WP-11 — Accessible, themeable and localizable UI

Deliverables:

- Minimum 44×44 effective touch targets for primary interactive controls.
- Non-color selected/focus states, semantic toggle/selection patterns and live-region announcements.
- Keyboard alternatives for signature stamps, editing and Organizer actions.
- Light, dark and High Contrast resources plus reduced-motion behavior.
- `.resw` resources, `x:Uid`, pseudo-localization, pluralization and RTL test language.

Acceptance criteria:

- Accessibility Insights reports no critical issues.
- A fixed Narrator/UIA script opens a file; enumerates, switches and closes tabs; enters the document; reads Unicode text in order; moves page-to-page with `Page X of Y`; invokes internal/external links; inspects/edits every supported form widget; traverses search snippets; prints; and closes.
- The script verifies Document/Text, Scroll, Selection, Invoke and native form patterns. Recycled peers never expose stale content or bounds.
- UI remains usable at 200% and 400% system text size.
- There are zero hard-coded user-visible strings in XAML, code or manifests outside test fixtures. Runtime messages use `ResourceLoader`; numbers/plurals use current culture.
- `en-US`, +40% pseudo-expanded and pseudo-RTL complete open/search/forms/print/settings scripts without clipping, incorrect mirroring or untranslated tokens.
- No selected or error state is communicated through color alone.

### WP-12 — Post-Stable transactional Organizer and annotation persistence

WP-12 is a non-critical Labs track and does not block the reader GA. GA builds and release tests run with Labs off; integrity/security suites additionally run with Labs on. With Labs off, Organizer/edit commands, accelerators and feature-specific recovery creation are absent.

Deliverables:

- Immutable page-plan model with reorder, rotate, delete, import, undo and redo operations.
- Organizer preview renders from page plan revisions without mutating the Reader session.
- Atomic export to a new file by default; overwrite is an explicit advanced action.
- Time-boxed decision spike for PDFium annotation APIs versus another approved writer.
- Non-destructive PDF annotation objects where supported; flattened export is an explicit separate option.

Acceptance criteria:

- Canceling Organizer leaves source PDFs byte-for-byte unchanged.
- Undo/redo is deterministic across mixed-document page plans.
- Exported PDFs reopen, retain expected page order/rotation and pass structural validation.
- Editing an annotated page does not silently rasterize or remove original selectable text, links or forms.

### WP-13B — Worker sandbox hardening

Deliverables:

- AppContainer/LPAC or an equivalently documented restricted-token design, current-user-only pipe ACL, random per-launch IPC secret and process mitigations.
- No-network capability set and brokered handles as the only file authority.
- Whole worker-process-tree Job Object: hard 512 MiB committed-memory defense limit, CPU-rate throttling and orphan termination. Product memory SLOs remain the tighter normal-operation gate.
- Heartbeat and operation-deadline watchdog that terminates an unresponsive worker within two seconds.
- Optional small process pool only after an ADR proves measured end-to-end benefit; each process retains a serialized PDFium lane and all workers share the aggregate memory/cache gates.

Acceptance criteria:

- A malformed PDF can terminate only its worker, never the UI process.
- The UI reports failure and remains usable after worker restart.
- The worker cannot open an arbitrary path, connect to loopback/internet, access another user's pipe/mapping or retain source/temp write authority; negative integration tests prove each denial.
- Committed memory above the hard cap is prevented, CPU consumption is throttled, and a missed heartbeat/deadline terminates the worker tree within two seconds.
- IPC rejects stale revisions, oversized payloads and protocol-version mismatches.
- The fuzz corpus produces no UI-process crash or indefinite hang.

### WP-14 — Packaging, CI and release operations

Deliverables:

- CI matrix for x64 and ARM64 restore, build, unit/integration tests, Native AOT publish and MSIX packaging.
- Hosted Windows x64 is the universal PR compile/package path. ARM64 execution tests run only on a trusted, physical
  self-hosted ARM64 runner and only for protected-branch pushes, scheduled runs, or protected-branch dispatches;
  pull-request refs and arbitrary workflow-dispatch refs must never be checked out on self-hosted hardware.
- Scheduled UI, accessibility, performance, memory and fuzz suites on dedicated Windows agents.
- Signed MSIX artifacts, automated SemVer-to-MSIX version mapping, symbols/SourceLink, SBOM, provenance, third-party notices and signed checksums.
- Microsoft Store submission and flighting, update/certificate-rotation behavior, offline enterprise-install documentation and forward-rollback procedure.
- Privacy-safe structured local logs, opt-in crash reports and redacted support bundle.
- Install, update, downgrade rejection, uninstall and file-association tests.

Acceptance criteria:

- The unsigned package payload and build manifest are reproducible from the tag and lock files; hashes, provenance and signing records trace each signed artifact to that payload.
- Package identity, publisher and architecture match signing configuration.
- Windows App Certification Kit checks pass.
- File activation works for cold start and redirects later activations into the running instance.
- A staged release can be halted and rolled back without losing user settings or recovery data.
- Tags `vM.m.p[-pre]` map to MSIX `M.m.p.build`. Rollback rebuilds the last-known-good payload with a strictly higher four-part package version and signs it anew; an older signed package is never installed directly.
- Local logs are capped at 20 MiB and seven days. Users can preview, export and delete a redacted support bundle; crash upload is a separate opt-in and rejects dumps containing document buffers.
- An offline smoke test observes no network traffic except an explicit update check or diagnostics the user opted into.

Rollout policy:

- Canary: internal signed build after all pull-request gates, followed by at least 24 hours of automated corpus and lifecycle soak.
- Beta: minimum seven days with no unresolved data-loss/security P0/P1 issues, zero critical accessibility issues and all performance budgets passing.
- Stable 5%: observe for at least 48 hours and 1,000 eligible sessions, including 100 ARM64 sessions; otherwise extend the ring. Stop automatically on crash-free rate below 99.8%, any verified integrity signal, or p95 startup/render regression over 10%.
- Stable 25% and 100%: meet the same minimum observation window/sample at each ring. Rollback uses a forward-versioned MSIX containing the last-known-good payload.

One verified original-file corruption or incorrect-document render stops rollout immediately. A hang means the UI dispatcher fails to complete a watchdog post for more than five seconds while foreground and outside an OS-owned modal picker/print UI. Session start/end/watchdog events use the same opt-in telemetry population and denominator; Store health is reported separately rather than mixed with application telemetry.

## 8. Test strategy

### 8.1 Required test layers

- Domain tests: state machines, revisions, page plans, cache keys and command rules.
- Application tests: workspace/tab lifecycle, cancellation, save/discard, activation and recovery.
- PDFium contract tests: open, metadata, render, search, forms, encryption, corrupt files and deterministic native-owner lifetime.
- Rendering tests: scheduler priority, deduplication, stale-result rejection, memory budgets and golden images.
- Storage tests: fault-injected atomic replacement, conflicts, permissions and recovery corruption.
- UI automation: open, navigate, search, select/copy, print, tabs, session restore, edit and Organizer preview.
- Accessibility tests: a fail-closed UIA contract report covers names/roles/patterns, keyboard focus and traversal,
  page-peer virtualization, and supported/unsupported form controls. High Contrast is an explicit automated host-state
  check. Narrator listening, Accessibility Insights findings, physical touch/pen, signed-install, and visual/text-scale
  review remain separately recorded manual gates; a UIA pass cannot satisfy those gates.
- Performance tests: launch, first page, scroll, zoom, random jump, search interference, memory release and soak.
- Packaging tests: clean install, upgrade, rollback, uninstall, file association and x64/ARM64 smoke tests.
- Security tests: dependency/provenance scan, IPC validation, malformed corpus, worker limits and fuzzing.

### 8.2 Pull-request gates

- Locked restore succeeds.
- x64 and ARM64 compile.
- Unit and fast integration tests pass.
- Native AOT publish succeeds.
- No new project warnings, analyzer errors or unapproved dependency changes.
- Performance smoke tests show no regression greater than 10% in tracked p95 metrics.
- Changed UI includes keyboard and automation-state tests.

### 8.3 Nightly and release-candidate gates

- Full corpus and golden render suite.
- UI automation on supported Windows builds and display scales.
- Accessibility scan and manual-script status.
- Cold/warm performance suite on dedicated hardware.
- Eight-hour mixed-document soak with repeated open/scroll/search/close cycles.
- Save fault injection, worker fuzzing and memory-pressure suite.
- Signed-package install/update/file-activation tests.

Required matrix dimensions include physical/virtual x64, a physical ARM64 device, the supported minimum Windows build, current Windows 11 GA and the latest Windows Insider preview; 100/150/200% DPI; keyboard, mouse, precision touchpad, touch and pen; Narrator, High Contrast, reduced motion/transparency and 100–400% text scaling; `en-US`, pseudo-expanded, pseudo-RTL and one CJK locale.

## 9. Instrumentation contract

At minimum, emit structured ETW events for:

- `AppLaunchStart`, `ShellInteractive`, `ActivationReceived`.
- `DocumentOpenStart`, `MetadataReady`, `FirstPageRequested`, `FirstPagePresented`.
- `RenderQueued`, `RenderStarted`, `RenderCompleted`, `RenderCancelled`, `RenderRejectedAsStale`.
- `PdfiumLaneWait`, `PdfiumCallDuration`.
- `PixelUploadDuration`, `FramePresented`.
- `CacheHit`, `CacheMiss`, `CacheEvicted`, `CacheBytes`.
- `SearchStarted`, `SearchPageCompleted`, `SearchResultPublished`, `SearchCancelled`.
- `SaveStageStarted`, `SaveStageCompleted`, `SaveFailed`, `RecoveryCheckpointed`.
- `WorkerStarted`, `WorkerRestarted`, `WorkerBudgetExceeded`, `WorkerCrashed`.

Events use random per-session document identifiers. They must never include paths, filenames, passwords, search queries, extracted text, annotations or signature-stamp data.

## 10. Delivery sequence and dependencies

| Sprint | Weeks | Primary packages | Exit outcome |
|---|---:|---|---|
| 0 | 1–2 | WP-00, WP-01 | Deterministic preview stack, supported-platform decision, corpus and frozen baseline |
| 1 | 3–4 | WP-02 | Original-file safety and correct content/recovery state |
| 2 | 5–6 | WP-03, WP-04 | Native ownership, transport DTOs and document-context seam |
| 3 | 7–8 | WP-13A | All production PDFium work crosses authenticated worker IPC |
| 4 | 9–10 | WP-06 | Bounded priority scheduler and request/publication semantics |
| 5 | 11–12 | WP-05 | Demand-driven virtualized page/automation host |
| 6 | 13–14 | WP-07 | Direct-pixel tiled display and bounded caches |
| 7 | 15–16 | WP-08 | Composition zoom, monitor DPI and prefetch |
| 8 | 17–18 | WP-09 search/semantics | Progressive search, selection, links, outline and properties |
| 9 | 19–20 | WP-09 forms, WP-10 shell | Round-tripped forms and adaptive reader interaction |
| 10 | 21–22 | WP-10 print/session | Bounded printing, restoration and privacy lifecycle |
| 11 | 23–24 | WP-11, WP-13B start | Accessibility/localization beta and restricted-worker verification |
| 12 | 25–26 | WP-13B, WP-14 | Fuzzed, hardened, signed release candidate |
| 13 | 27–28 | Stabilization/rollout | Soak, release-matrix closure and first Stable ring |

Milestone gates:

- **M0 Safety Foundation (week 4):** WP-02 passes all fault injection; no production command can write a destination outside `IAtomicDocumentStore`.
- **M1 Rendering Alpha (week 14):** x64/ARM64 worker boundary, scheduler, virtualization and direct tiles operate on the corpus; the 10,000-page and memory-bound architecture gates pass, and tracked latency is within 20% of final SLOs.
- **M2 Reader Beta (week 22):** every GA reader capability is feature-complete with Labs off; forms round-trip, printing is bounded, session/privacy lifecycle works and no P0/P1 integrity/security defect is open.
- **M3 Release Candidate (week 26):** every Section 4 SLO and the complete signed matrix pass; fuzz, accessibility, localization and eight-hour soak evidence is attached to the release record.
- **M4 Stable (weeks 27–28+):** Canary/Beta and first Store ring satisfy their observation/sample gates. Calendar week never overrides a rollout stop condition.

Critical path:

```text
Preview governance and benchmark baseline
  → atomic save/state correctness
  → DocumentContext, transport contracts and worker boundary
  → priority scheduler and virtualized host
  → direct tiled renderer
  → semantic/accessibility layer
  → worker hardening
  → signed release candidate
```

Packaging, CI, provenance and instrumentation progress continuously rather than waiting for the final sprint.

## 11. Release readiness checklist

A public production build may ship only when:

- No open P0 data-loss, security, crash, hang or incorrect-document issues exist.
- All performance SLOs pass on the frozen reference hardware and corpus.
- x64 and ARM64 signed packages pass the full release matrix.
- Save fault injection reports zero damaged originals.
- The malformed/fuzz corpus cannot crash or hang the UI process.
- Zero critical/high accessibility defects exist. Only low/medium defects or verified tool false positives may be waived with rationale, owner and expiry; no waiver may block a primary workflow.
- Privacy documentation accurately describes recents, recovery data and diagnostics.
- PDFium provenance, SBOM, licenses and hashes are published with the release.
- The newest selected .NET 11/toolchain preview has passed the complete compatibility pipeline.
- Staged rollout, monitoring and rollback have been exercised with a release candidate.
- GA tests pass with Labs off; integrity/security tests also pass with Labs on.
- The selected GA distribution vehicle has passed install, update, certificate rotation, offline and forward-rollback drills.

## 12. Key risks and mitigations

| Risk | Mitigation |
|---|---|
| .NET 11 or package preview regression | Exact pins, weekly compatibility branch, Native AOT CI, full gate before promotion, last-known-good rollback artifact |
| PDFium is single-threaded at API level | One priority engine lane per process; page-sized jobs; separate processes only when measured parallelism is needed |
| Direct GPU surface path becomes too complex | Time-boxed backend spike; ship direct pooled BGRA upload first if it meets SLOs |
| Semantic text/a11y layer is larger than expected | Start geometry extraction with rendering work; make selectable text and Narrator a release gate, not late polish |
| Worker IPC delays schedule | Freeze transport DTOs in WP-04, establish the brokered worker in WP-13A and route all later rendering/semantic work through it |
| Editing scope distracts from reader quality | Reader-first production scope; Organizer/editing remain preview until their independent gates pass |
| Preview dependencies enlarge package | Inspect publish payload on every update; exclude unused optional components and symbols from distribution |
| Untrusted or pathological PDFs exhaust resources | Tiled rendering, checked limits, progressive cancellation, worker Job Object budgets and fuzz corpus |

## 13. First twelve implementation tasks

1. Add exact preview SDK/package governance and locked restore.
2. Add benchmark corpus manifest and first-page/scroll/memory ETW events.
3. Introduce stable document/page identity, the content-state reducer and revision/cache-key tests.
4. Implement atomic save-to-temp, validation and replacement behind `IAtomicDocumentStore`.
5. Route Save, Save As, close and Discard through the coordinator; disable destructive Labs-off commands.
6. Add architecture-correct PDFium assets and make ARM64 build/publish real.
7. Add deterministic on-engine-lane native owners and remove current-directory DLL lookup.
8. Introduce `DocumentContext`, transport DTOs and the `IPdfEngineClient` seam.
9. Establish authenticated worker launch, brokered handles, shared-memory leases and crash recovery.
10. Add the bounded priority scheduler and latest-generation publication rules.
11. Replace the continuous `ItemsControl` with the virtualized page/automation host.
12. Implement fixed-tile direct BGRA rendering and remove PNG from the on-screen path.

The first production milestone is complete only after tasks 1–7 and all WP-02 acceptance criteria pass. Rendering work must not be used to defer data-integrity fixes. WP-12 starts only after the reader reaches Stable unless separate staffing keeps it entirely off the critical path.
