# UIA/Narrator release procedure

Run only on a dedicated interactive Windows 11 desktop with a signed install and the synthetic corpus generated from
`testdata/manifest.json`. `eng/Run-UiAccessibility.ps1 -Interactive -Execute` is the executable, fail-closed UIA contract.
For the complete automated gate, pass `-RequireFixture -FixturePath testdata/generated/synthetic-mixed-orientation-links-forms-outlines.pdf
-FixturePageCount 8 -SecondaryFixturePath testdata/generated/synthetic-1000-pages.pdf -SecondaryFixturePageCount 1000`; pass
`-RequireHighContrast` for the High Contrast run. The contract:
it launches ElliePdf, optionally opens synthetic fixtures through file activation, verifies named controls and UIA patterns,
checks keyboard focus/tab traversal, validates `Page X of Y` identity, checks demand-driven page peers and form patterns,
and records a privacy-safe JSON result with no paths, filenames, extracted text, or form values. A skipped fixture or High Contrast
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
