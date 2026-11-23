# Visual Studio local testing readiness

Status: Planned; implementation and interactive verification remain outstanding.
Baseline: `4184526`, inspected 2026-09-07.

## Outcome

On a supported Windows x64 development machine, open `ElliePdf.slnx` in the compatible Visual Studio Insiders installation, select ElliePdf / Debug / x64 / ElliePdf (Package), and press F5. The solution restores and builds, deploys the app and its private worker, opens a PDF, and supports ordinary reader testing without shell environment overrides or prebuilt worker files. A managed UI breakpoint must bind and be hit.

This milestone establishes local testing readiness. It does not complete the performance, accessibility, signing, Store, or physical ARM64 release gates in `EXECUTION_SPEC.md`.

## Constraints and evidence

- Preserve .NET 11 preview, the compatible preview/experimental dependencies, and the exact version pins. Toolchain upgrades continue through the existing preview-update process.
- Preserve NativeAOT release publishing and parser isolation; the worker remains self-contained.
- The previously observed normal Visual Studio Debug build fails with NETSDK1151: the framework-dependent UI has a build-only reference to the self-contained worker despite `ReferenceOutputAssembly=false`.
- A temporary executable-reference validation override allowed a command-line Debug build. Visual Studio subsequently reported build success using prebuilt outputs, but skipped the worker and still reported a Benchmark restore/project-loading error. That is not evidence of a working fresh F5 flow.
- Visual Studio automatic restore previously removed the root lock file's package entries. The diagnostic mutation was reverted. The root cause remains unconfirmed; merely forcing locked mode prevents mutation but does not prove restore works.
- Both Package and Unpackaged profiles are advertised. The project does not explicitly configure `WindowsPackageType=None` for the latter; its runtime initialization must be verified.
- Release CI previously passed; neither interactive F5 nor actual PDF reading under the debugger has been verified.

## Work packages

### LD-01: Reproduce IDE restore and project loading

Inspect a fresh checkout with no `.vs`, `bin`, or `obj` state using the installed compatible Visual Studio Insiders. Capture IDE/MSBuild restore diagnostics and compare the evaluated NuGet inputs with a locked CLI restore. Investigate the Benchmark project-loading error and the missing PackageReference entries; establish whether they share a cause. Fix the evaluation/loading issue and make routine development restore preserve the committed dependency graph. Retain an explicit dependency-update route for intentional package changes.

Files: `Directory.Build.props`, `ElliePdf.slnx`, `ElliePdf.csproj`, and package configuration only where the diagnosis requires it.

Acceptance: normal IDE restore completes without errors; all projects load; opening, restoring, and switching Debug/Release leave every committed lock file unchanged. No reliance on a previous CLI restore.

### LD-02: Fix Debug build and worker scheduling

Represent the UI-to-worker relationship as a build/deployment dependency without treating the worker as an in-process executable dependency. If an SDK validation exemption is required, scope it to this intentional relationship and document the reason. Diagnose why Visual Studio skipped the worker, including the worker's declared platforms and solution Build mappings. Ensure x64/ARM64 configuration propagation and worker build ordering are correct in the IDE and CLI.

Files: `ElliePdf.csproj`, `ElliePdf.slnx`, `src/ElliePdf.Pdfium.Worker/ElliePdf.Pdfium.Worker.csproj`.

Acceptance: fresh Debug x64 build succeeds without environment overrides; worker executable, managed dependencies, runtime files, and matching PDFium asset are generated automatically. Editing worker code triggers its rebuild on the next app build. UI breakpoints bind. Debug ARM64 compiles with the matching native bundle; execution is tested separately on ARM64 hardware.

### LD-03: Make launch and deployment complete

Use Package as the primary F5 profile. Ensure the Debug worker bundle enters the actual deployment recipe before deployment, rather than merely appearing in the build output after packaging targets have run. Validate Windows App SDK dependencies and development registration. Verify cold F5, stop/restart, and Ctrl+F5 behavior. Diagnose activation redirection if an existing ElliePdf instance captures a debug launch.

Verify the Unpackaged profile with explicit, consistent build and runtime initialization settings. If it cannot be supported in this milestone, remove it and its README promise; do not leave a selectable broken profile.

Files: `Properties/launchSettings.json`, `ElliePdf.csproj`, `Package.appxmanifest`, and activation code only if runtime evidence requires it.

Acceptance: the supported profiles launch from Visual Studio; the deployed payload contains the correct worker; opening a document starts that worker successfully; app shutdown leaves no worker orphan. No manual DLL copying, release publish, or release signing credentials are needed for local debugging.

### LD-04: Verify the reader interactively

Use the deterministic synthetic corpus and temporary copies for write tests. Exercise startup, file picker open, first-page rendering, scrolling, page navigation, zoom, text selection/copy, search, tab switching/closing, and reopening. Exercise encrypted PDFs (correct and incorrect password), corrupt PDFs, and the 1,000-page fixture. Check a form edit saved to a temporary copy and reopened. Fix defects that prevent this local reader smoke test from completing.

Use applicable portions of the existing UI automation tooling, while respecting its signed-install and dedicated-desktop prerequisites. Add a focused development smoke entry point only if those prerequisites prevent reuse. Confirm the visible rendered page as well as process liveness and control presence.

Acceptance: record the commit, toolchain, launch profile, fixture, expected result, actual result, and any failure. Demonstrate a UI breakpoint hit, a readable PDF, responsive navigation, and a clean stop/relaunch. Leave the app ready for the user to test.

### LD-05: Prevent build and payload regressions

Add fresh Debug x64/ARM64 build coverage to CI alongside existing Release coverage. Check that restore/build leave lock files unchanged and that the Debug deployment payload contains the architecture-matched worker. Add behavioral checks for the specific failures discovered; avoid tests that only match project-file strings. Hosted build checks must not claim interactive F5 coverage.

Acceptance: Debug builds and existing Release builds/publishes pass on the final commit. Interactive validation remains a separately recorded result. Physical ARM64 execution is explicitly marked pending if suitable hardware is unavailable.

### LD-06: Document and deliver

Update README with verified prerequisites, solution/startup project, configuration, supported profile, F5/Ctrl+F5 steps, and short troubleshooting instructions. Remove temporary shell overrides from the recommended route. Record remaining production release gates separately. Commit the implementation in reviewable groups, push to the existing branch, and update draft PR #5 with validation evidence.

Acceptance: a developer can follow the README from a fresh checkout; final PR checks pass; the handoff states exactly which launch paths were exercised and which checks remain outstanding.

## Sequence and completion gate

LD-01 and LD-02 can be investigated independently, with coordinated edits to shared project files. LD-03 follows their fixes; LD-04 follows a working launch. LD-05 can proceed alongside LD-03/04 once the build behavior is settled. LD-06 closes the milestone after verification.

Mark local testing ready only when LD-01 through LD-06 pass for x64, including actual Visual Studio F5 deployment, a debugger breakpoint, visible PDF reading, and stop/relaunch from fresh build outputs. A green Release pipeline or a build that reuses an existing worker is insufficient.
