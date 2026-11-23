# UIA/Narrator release procedure

Run only on a dedicated interactive Windows 11 desktop with a signed install and the synthetic corpus generated from
`testdata/manifest.json`. `eng/Run-UiAccessibility.ps1 -Interactive -Execute` is the executable, fail-closed UIA contract.

After successful protected signing, the release workflow runs this contract on native x64 and ARM64 interactive runners.
`eng/Invoke-SignedUiValidation.ps1` verifies the same-run signed artifact and detached checksum record, checks the installed
executable against the MSIX, and records the package hash beside the UIA report. The runners need the public signing
certificate and Windows App SDK dependencies already installed. No signing credentials are passed to the UIA jobs.
The nightly CI desktop jobs remain separate from this signed release gate.

Use `-InstalledAumid '<package-family-name>!<application-id>'` for the signed, installed package. Obtain the package family
from `Get-AppxPackage` for the intended ElliePdf install and the application Id from its `Get-AppxPackageManifest` output;
do not substitute the package full name or display name. For example, after identifying the intended package:

```powershell
$package = Get-AppxPackage -Name '<exact installed ElliePdf package identity name>'
$manifest = Get-AppxPackageManifest -Package $package.PackageFullName
$manifest.Package.Applications.Application | Select-Object Id, Executable
# Select the intended Application Id from the manifest if the package has multiple applications.
$aumid = "$($package.PackageFamilyName)!<application-id>"
./eng/Run-UiAccessibility.ps1 -Interactive -Execute -InstalledAumid $aumid `
    -TargetPath '<absolute installed ElliePdf.exe path>' `
    -RequireFixture -FixturePath testdata/generated/synthetic-mixed-orientation-links-forms-outlines.pdf `
    -FixturePageCount 8 -SecondaryFixturePath testdata/generated/synthetic-1000-pages.pdf `
    -SecondaryFixturePageCount 1000
```

Installed mode verifies the current-user package manifest, uses Windows `IApplicationActivationManager.ActivateApplication`,
and directs both fixture opens to the same AUMID with `ActivateForFile`. It never changes or relies on the default PDF
association. The returned process must have the requested application identity; optional `-TargetPath` additionally pins
the actual executable path. Any mismatch or inaccessible ownership information fails before UIA interaction. A file open
returning a different process also fails. Close existing ElliePdf instances before running: activation reusing a preexisting
process is rejected and that process is not terminated. Only a newly launched, verified process is eligible for cleanup.

Legacy `-TargetPath` without `-InstalledAumid` retains direct executable launch and default-association fixture opens for
existing callers. Use installed mode for packaged NativeAOT builds: directly starting the EXE does not supply the package
identity required by Windows App SDK deployment initialization. There is no automatic fallback from installed activation
to direct launch or default-association file opening.

The dedicated runner still needs the signed package installed for its interactive user, a trusted signing certificate,
the generated synthetic fixtures, and an idle interactive desktop. This script does not install or sign packages, configure
the runner, or satisfy the manual signed-install release gate. Noninteractive CI and helper compilation do not establish
an interactive UIA pass.

For the complete automated gate, pass `-RequireFixture -FixturePath testdata/generated/synthetic-mixed-orientation-links-forms-outlines.pdf
-FixturePageCount 8 -SecondaryFixturePath testdata/generated/synthetic-1000-pages.pdf -SecondaryFixturePageCount 1000`; pass
`-RequireHighContrast` for the High Contrast run. The contract:
it launches ElliePdf, optionally opens synthetic fixtures through file activation, verifies named controls and UIA patterns,
checks keyboard focus/tab traversal, validates `Page X of Y` identity, checks demand-driven page peers and form patterns,
and records a privacy-safe JSON result with no paths, fixture filenames, extracted text, or form values. The target executable
basename (when provided), activation mode, and ownership verification outcome are recorded; AUMIDs are not recorded. A skipped fixture or High Contrast
check is explicitly recorded and cannot be represented as a complete contract pass.

1. Run the script once per display scale at 100%, 150%, and 200%. Keep the generated JSON report with the release evidence.
2. Run the script once with `-RequireHighContrast` enabled and once with the two fixtures above so tab switch/close, virtualization,
   and supported/unsupported form behavior are covered by automation.
3. Start Narrator and repeat the primary reader workflow manually. Confirm the window, Open command, tabs, page host, page count,
   search box, and Settings page have meaningful names and roles, and that page announcements remain ordered as `Page X of Y`.
4. Open the vector, CJK, mixed-feature, encrypted, 1,000-page, 10,000-page, huge MediaBox, corrupt, and parser-stress fixtures.
   Exercise internal and external links, every supported form widget, search next/previous/cancel, print, tab switching, close,
   and session restore. Confirm blocked or unsupported actions are announced.
5. On the text layer, select a word with double-click, a visual line with triple-click, drag across text spans and cross-page
   boundaries, then copy the ordered text. Confirm links and form hit targets remain independently focusable while text is selected.
6. Repeat the primary workflow with touch/pen where available. Physical touch targets, pen parity, and DPI-specific gesture behavior
   remain external release checks and are not claimed by the script.
7. Run Accessibility Insights against the signed build and attach the findings. Zero critical/high issues are required.

Evidence is attached to the release record by the operator. The JSON report deliberately separates automated UIA checks from
`required-manual` gates: a UIA script pass does not replace Narrator listening, Accessibility Insights review, physical touch/pen
verification, High Contrast visual review, or signed-install validation.
