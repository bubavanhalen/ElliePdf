# UX recovery: implementation and evidence

Date: 2026-09-07. Working tree based on `4184526`; changes are not yet committed.

## Scope of this increment

R0 source audit, an opt-in R1 native shell prototype, and the reproduced R2 worker/interaction defects. This is **not** completion of the recovery plan and is **not** production acceptance.

- [Regression register](UX_REGRESSION_REGISTER.md): original/current source inventory and proposed feature dispositions. The pre-rewrite visual comparison and user parity decisions remain open.
- Native review profile: **ElliePdf (Design preview)** in Visual Studio, Debug x64. The regular **ElliePdf (Package)** profile remains available. The preview flag is deliberately ignored in Release builds.
- The preview reuses the real PDF rendering, document commands, tabs and dialogs. It is not a static mockup. It separates title, tabs, commands, reading content and status into structural rows, promotes labeled reading commands, and includes a light/dark/system comparison control.
- The proposed design is not approved. Do not promote the preview to the default UI until the R1 review is complete.

## Reproduced defects and changes

### Semantic responses could not cross the worker boundary

The real-worker vector test failed while deserializing permissions: `PdfPermissions` had multiple public constructors without an explicit JSON constructor. Once fixed, the same test exposed a second failure: the `PageLinks` constructor parameter used `IEnumerable<PdfLink>` while its property used `ImmutableArray<PdfLink>`.

The first failure left `CanCopy` false. The second prevented publication of the semantic page, including selectable text. Both could be hidden by background fault handling even while raster pages and search worked.

Changes: explicit JSON constructors with compatible parameter types; source-generated transport round-trip tests for permissions and empty/nonempty links; a real-worker test of text → permissions → links → forms on the vector fixture. The earlier hypothesis about raster cancellation was not sufficient to explain the failure; its speculative ViewModel change was removed.

This repairs transport prerequisites for selection. Per-line native text controls still do not implement a complete multi-line/cross-page selection experience. Do not mark that broader requirement passed.

### Password/open operation ownership

Changes: password dialogs dispatch all UI access, report missing hosts as failures, distinguish an empty password from cancellation, serialize concurrent password dialogs, and wait for active dialogs to unwind on cancellation. Document opens no longer invoke the session factory under the workspace-wide gate. Shared opens, cancellation, retries, attach races and runner cleanup have behavioral regression coverage.

Desktop password acceptance still requires the complete correct/wrong/empty/cancel/retry journey. Source-string boundary checks are architectural guards only, not proof of dialog behavior.

The desktop retry still showed no prompt after the UI-dispatch repair. A real-worker regression then proved a second root cause: brokered PDF loading disposed custom file access before reading `FPDF_GetLastError()`, losing the password error and reporting `worker_error`. Capture the native error before cleanup. The real-worker test now passes missing/wrong/correct-password cases; the UI journey must still be checked separately.

### Reading panes

Clicking the document no longer automatically closes a docked pane. Narrow transient panes retain light-dismiss behavior. The preview uses a native TitleBar for caption reservations; the regular shell's earlier fixed-margin workaround is not being declared a final design solution.

## Desktop evidence from this increment

Configuration: working-tree Debug x64 MSIX deployment, Visual Studio Insiders F5, native design-preview profile, synthetic fixtures only. These are targeted smoke checks, not the complete journey matrix. The debugger's XAML toolbar is visible in development captures and is not app chrome.

- Vector fixture: native line selection and Ctrl+C produced the exact expected line, including all digits. The transport failures were reproduced before fixing them. Focused text controls no longer paint a dark input rectangle over the PDF; selection is blended over the raster. Precise glyph alignment, multi-line/cross-page selection, High Contrast and all zoom levels remain unaccepted.
- Pages: visible labeled command opened the three thumbnails; the pane close control closed the pane without closing the application in the preview.
- Encrypted fixture: after preserving the native password error, the app displayed the prompt; empty and wrong passwords displayed retry messages; Cancel left the vector document open and showed “Open cancelled”; reopening and entering the correct fixture password opened a second tab with two pages.
- **New unresolved failure:** returning to the vector tab after that password journey reported “The PDF worker session ended and must be reopened.” The raster remained visible, but this is not successful preservation of a working tab. A new real-worker regression preserves vector metadata/render across missing/wrong/correct encrypted opens and passes; the desktop restart therefore remains unexplained, not fixed by that test. Capture the restart/timeout/transport event and check tab zoom restoration next. Do not mark the whole password/multi-tab journey passed.
- Theme switch initially exposed black header text on a dark background. Moving the theme override to the window root repaired the header surface. Native caption theme synchronization also passed the final light-theme desktop rerun: header text and minimize/maximize/close glyphs remain visible.
- F5 launched the integrated preview. A Ctrl+F5 attempt reported that deployment was required; the solution also still shows an unknown-project-configuration-mappings banner. An earlier test session produced a native JIT-debugger warning during shutdown. These are unresolved launch/shutdown diagnostics, not hidden passes.

Automated verification so far: core tests 72/72; application tests 37/37; semantics tests 13/13; transport tests 17/17; targeted real-worker semantic, encrypted-open and prior-session preservation regressions pass. Final Debug x64 command-line build, including caption-theme synchronization, passed with zero warnings/errors. Development payload validation passed (286 recipe entries, x64). Full current-tree Release/ARM64/package and performance matrices have not been rerun.

Final native captures: [dark](evidence/ux-recovery-2026-09-07/native-preview-dark.png), [light](evidence/ux-recovery-2026-09-07/native-preview-light.png). These show the real Debug application, not an approved final design; the small top-center overlay belongs to Visual Studio's XAML debugging tools. Final clipboard check changed from `brown ` (native double-click includes a trailing space) to the exact full fixture line, ruling out a stale clipboard result.

## November 23 follow-up

- Worker transport now finishes a frame after caller cancellation, or tears down that worker generation if the write deadline expires. Cleanup also limits pending-request failure to the generation being retired. Worker-side cancel and disposal now serialize for each operation.
- The new frame test cancels the caller just after the length prefix and verifies that the complete frame can still be decoded. All 24 worker-client tests passed in an isolated x64 .NET 11 RC 3 candidate with the synthetic corpus. The original desktop tab-switch failure and per-tab zoom still need a replay.
- NuGet audit now rejects Preview 7 ILCompiler under GHSA-p3jp-9v83-76w9. Preview discovery now ranks release candidates above previews of the same version. The isolated candidate selected .NET SDK 11.0.100-rc.3.26522.148 and Windows App SDK 2.6.6-experimental and passed lock regeneration and locked restore.
- Seven CsWinRT diagnostics were resolved by passing concrete arrays. The 2.6.6-experimental candidate builds in Debug and Release after explicit registration of the standard WinUI XAML metadata provider. Unused experimental Chart and TableView providers are no longer emitted. The build uses no warning suppression.
- The isolated .NET 11 RC 3 candidate with the existing Windows App SDK 2.4.1-experimental pin passed locked restore and a Debug x64 app build with zero warnings or errors. The rebuilt app with the tab-zoom change also passed x64 development payload validation (286 recipe entries).
- Opening a file that already has a tab no longer reapplies default zoom. Fresh tabs get that default at creation, and the zoom label only uses the continuous renderer's cached scale if it belongs to the active document. Mixed-page fit dimensions and the desktop replay remain open.
- Continuous zoom placeholders only reuse tiles and semantic state from the same document. Continuous render completion also checks the active document. Single-page mode clears old-document tiles and checks document and page identity before publishing page dimensions.
- CI and the preview workflow now generate and verify the synthetic corpus before running the jobs that use it. A fresh generation matched all ten normal hashes and the 1 GiB fixture was verified separately. The large fixture is generated for the performance job.
- The isolated .NET 11 RC 3 / Windows App SDK 2.6.6-experimental candidate produced an unsigned x64 NativeAOT MSIX. The optimized AOT publish path had skipped WinUI reference resolution, so the app now uses the full-build publish path. PE inspection of the package confirms AMD64 native images for the app, worker and PDFium. Package SHA-256 `32E80A5C33C0EE6981D2E87DC5A4DB6226C2B78C1FEE3E62BB4D6E9CE2B6CDB2`. ARM64 native publish requires the ARM64 C++ component missing from this Visual Studio installation. Branch pin promotion and signed runtime validation remain open.
- Candidate verification also passed the ARM64 Release solution build, all 18 x64 test projects (368 passed, 3 skipped), and portable-PDB SourceLink verification. The benchmark backend self-test completed 30 measured iterations and 3 warmups. Product performance and accessibility still require executable and interactive evidence.
- Draft PR #5 had green x64/ARM64 build and unsigned publish checks on `4184526`. ARM64 runtime, scheduled performance and desktop UI checks were skipped on that run. The newer run for `58f2a67` failed locked restore for both architectures when NuGet audit rejected the Preview 7 ILCompiler dependency.

## Packaged runtime and diagnostics follow-up

- The x64 candidate binaries ran under a separate development package identity after installing the Microsoft-signed 2.6.6 experimental framework dependency side by side. Windows package activation reached the real reader window and restored the synthetic vector PDF at 80% zoom. The Open button displayed the file picker. The encrypted tab replay stopped when the desktop was in use. The existing ElliePdf package registration still points at the original workspace.
- Running the native app as a bare executable failed before the UI with a Windows App SDK activation exception. The UIA harness now supports explicit installed package activation and targets fixture file activation to that same package instead of relying on the default PDF association. The helper compiled and passed preflight checks. Signed, interactive runner validation is still required.
- The original x64 candidate passed the static MSIX inspection (276 entries), and its release SBOM passed validation with 41 components. The hosted compile/package workflows now use the Windows 2025 image whose published inventory includes the local machine's missing MSVC ARM64 cross-linker.
- NativeAOT had defaulted EventSource off. The app enables it explicitly. The private fixed-width payload writer has a local trim-analysis annotation for its primitive-only data. A NativeAOT test executable using ElliePdf's real provider produced and exported `FirstPagePresented`, `RenderCompleted` and `SaveStage` with the expected numeric and Boolean values. This validates the provider and exporter on the candidate SDK. It does not establish performance SLOs or prove that the packaged app emits all required events.
- The trace-enabled x64 NativeAOT rebuild passed static MSIX inspection (276 entries), SHA-256 `4BECE4E5337733D1B48D907F2D1EC5F65BF47C207565D9B15213E8CCCB07D892`. The two UIA contract tests passed, with the interactive check skipped. The native development activation above was on the earlier x64 package.

## Signed accessibility automation follow-up

- Commit `58f2a67` is pushed to draft PR #5. Successful protected signing now feeds same-run x64 and ARM64 interactive jobs. Each job checks provenance, the detached signed checksum record, package contents and signature, then verifies the installed executable and activates that package explicitly. The interactive jobs do not receive signing credentials.
- Actionlint passed for the signing, preview and CI workflows. The signed UIA preflight rejected four negative cases using the real unsigned candidate as input: stale run attempt, wrong package hash, tampered SBOM and an unsigned MSIX. Protected runner execution and the manual accessibility matrix remain open.
- The Release save-integrity harness completed all 10,000 forced-termination cases in the isolated RC 3 candidate. All 11 save stages passed. Invalid or missing destinations, unreached boundaries, failed child terminations, journal parse failures and unknown outcomes were all zero. Report SHA-256 `6B75DB1AC9DC90838619DCDC4AEA7662F96DFD2C51C37234DD3CCD4E8A786A19`.
- The read-only preview candidate job now runs on draft PRs. It records the tested pin and lock patch, toolchain fingerprint, SBOM, and hashes of the inspected x64 and ARM64 packages. Updating the proposal branch is limited to scheduled or manual default-branch runs after candidate validation. The committed toolchain still requires promotion after the candidate passes the governance gates.

## Hosted preview and reader reliability follow-up

- Hosted RC 3 built x64 and found a test that raced session disposal completion. The application suite passed locally with the synchronization corrected, including 24 repeated runs of the affected test.
- The next hosted run found a broken pipe during worker startup and an input file still held open during fuzz cleanup. The worker now initializes PDFium before connecting its pipe, the broker observes early exit within its startup deadline, and the fuzz harness cleans up temporary files after worker exit. The local Release fuzz run before these changes covered 1,024 inputs with zero crashes, deadlines, unexpected outcomes, or leaked workers. The updated worker passed the five-test fuzz smoke and the 24-test client suite locally.
- A one-minute x64 lifecycle smoke completed 219 cycles and 4,376 operations with zero worker restarts. A relative report-path bug found during the run is fixed. The eight-hour soak remains a release gate.
- Continuous fit zoom now uses metadata for the active tab's current page. An outdated layout request cannot publish dimensions after a tab or view change. The mixed-page tab interaction still needs desktop replay.
- Scheduled reference performance now requires 48 ETW reports covering 100%, 150%, and 200% display scaling, tied to the same-run x64 MSIX. A deterministic 14.8 MB raster fixture covers cold activation. The verifier passed synthetic positive and rejection tests. Product benchmarks and frozen competitor baselines remain open.

## Native x64 desktop replay

- A separate development identity ran the unsigned RC 3 NativeAOT build. Opening the mixed-orientation and encrypted fixtures through the picker worked. The encrypted file raised the expected dialog and rendered after unlock. Keyboard cycling reached the prior documents and restored the vector fixture's 80% zoom.
- Inactive labels disappeared where the tab strip overlaid a white page. The strip now uses an opaque theme surface and equal-width tabs. A fresh packaged replay showed all three labels, and clicking the vector tab restored its contents and zoom. Tabs and commands still need a complete layout review because the default shell overlays page content.
- The mixed fixture showed doubled form text from the translucent editor over the PDF raster. An opaque theme surface behind the native text editor removed the duplicate on the packaged replay. Form persistence and the accessibility matrix remain separate checks.
- Hosted Windows still reported first-request broken pipes after native readiness checks. The worker now verifies the broker pipe owner's actual user SID, including elevated tokens whose default owner differs. Locally the fuzz smoke (5), worker-client tests (24), and transport tests (20) passed. The first hosted RC 3 run then passed all x64 tests, both architecture builds, both NativeAOT packages, and static MSIX inspection. The repeat also passed the worker tests and failed a rendering scheduler check. The scheduler now decrements the busy count before releasing result waiters.
- The default tab strip now has a header row above the page and shows the document title even with one tab. This layout passed locked Debug x64 restore, build, development payload and unsigned NativeAOT MSIX validation. The packaged desktop checks above were on the preceding layout, where tabs still overlaid page content.
- All 35 rendering tests passed with the scheduler fix. The affected test passed 24 additional runs and now asserts that both render requests publish successfully. The exact pin and lock artifact from hosted run `45194076980` attempt 1 is staged on the draft branch for the regular CI matrix. Promotion still requires consecutive green runs and the release gates.

## Remaining review checklist

- Confirm the desired design direction against the earlier app, not just against the regressed shell.
- Review one-document identity, Pages/Outline/Find discovery, page/zoom controls, normal and minimum widths, light/dark/High Contrast, keyboard focus and text scaling.
- Finish selection accuracy, multi-line/cross-page selection and clipboard checks after zoom/scroll/tab changes.
- Complete password, forms/save/reopen, print/cancel, tabs and recovery journeys.
- Validate speed on the same final package used for design and usability review. No speed claim is made from these development/debug runs.
- Re-run the full build/test/architecture/package matrix after integration, then produce reviewable commits and update the existing draft PR when the milestone is validated.

Governing acceptance: [UX_DESIGN_RECOVERY_PLAN.md](UX_DESIGN_RECOVERY_PLAN.md). Speed, UX and design remain independent mandatory gates.
