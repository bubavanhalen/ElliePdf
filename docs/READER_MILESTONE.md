# Reader milestone

Draft [PR #5](https://github.com/bubavanhalen/ElliePdf/pull/5) carries this work. The starting point is `1ad1004`. The pre-rewrite reader is `363243e`, the merge base with `master`. [Execution spec](../EXECUTION_SPEC.md) and [recovery evidence](UX_RECOVERY_PROGRESS.md) document earlier work.

| Order | Deliverable | Acceptance |
| --- | --- | --- |
| 1 | Establish speed on the same Windows desktop | For each viewer and PDF, report raw samples, timeouts, p50 and p95 from file dispatch to the first displayed frame with readable document content. Keep cold process, warm reopen and resident viewer with another tab separate. Record file and application cache conditions. Measure sustained scrolling, zoom to sharp content and a distant page jump in the same long PDF. ElliePdf must beat both browsers at p50 and p95 in each open condition. |
| 2 | Fix the highest-impact reading failures | Run the same file open, search, selection/copy, jump, zoom/fit, pages/outline, links, printing and multi-tab restoration journeys in the real app. Verify local and web PDF entry points. Compare with the earlier ElliePdf build and the browser viewers. Mark usability and visual quality separately, including keyboard-only operation, light and dark themes, and 100%, 150% and 200% scaling at narrow and normal widths. |
| 3 | Fill and sign | Fill native fields and add text to a flat form. Import a PNG signature and draw a signature, then move and resize them. Reopen the saved copy in a second viewer and check field values and visible ink. Compare the original file hash before and after. |
| 4 | Merge and rearrange | Combine two files, move and remove pages in the thumbnail workspace and export. Reopen the result and verify page count, order and rotation. Compare both source hashes before and after. |

Use at least 30 measured samples per fixture, viewer and condition for acceptance. Interleave viewers and retain every trial. A small local pilot can guide investigation while the full matrix is pending. Compare with the Windows display as the shared observation point. The in-process timing and synthetic corpus remain useful for profiling and regression checks.

## Open release gates

- The historical [x64 fuzz failure](https://github.com/bubavanhalen/ElliePdf/actions/runs/45197343494) was one unexpected outcome. The CI assertion did not identify the case or exception. The next [x64 CI run](https://github.com/bubavanhalen/ElliePdf/actions/runs/45198681258) passed fuzz smoke and failed the separate flattened-annotation test. Keep the intermittent worker gate open until repeated hosted smoke and a full 1,024-case run pass with the same release toolchain and publish candidate.
- The packaged reader needs a signed interactive replay, including ARM64. Compare the two ElliePdf builds and establish readable-frame timings before picking a shell redesign. Keep the PDFium worker and atomic-save pipeline as the starting point.
- Public release and Store submission require a separate decision.

## November 23 local pilot

Windows 11 Insider build 26340 on an Intel Core i7-12700F (12 cores, 20 logical processors), 32 GB RAM and NVIDIA GeForce RTX 3060, driver `32.0.16.1742`. The display was 3440 × 1440 at 99 Hz, and the browser windows were 1086 × 753 at 100% scaling. Other desktop processes were running. The historical fuzz replay overlapped part of the browser pilot, so repeat the pilot on an otherwise idle desktop before using it for acceptance.

| PDF | Pages | Bytes | SHA-256 |
| --- | ---: | ---: | --- |
| [RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.pdf) | 194 | 2,858,365 | `60B30EFA1048900833D1758440247FE8AC85A3134F2327388DCB24E07D814C89` |
| [Apollo 11 Mission Report](https://www.nasa.gov/wp-content/uploads/static/apollo50th/pdf/A11_MissionReport.pdf) | 359 | 11,835,694 | `5B9D033C7B9DD604220F439CDDC8A53AD08E3E9C43C83F7342D7E61EF32A1BAA` |
| [Apollo 16 PAO Commentary](https://www.nasa.gov/wp-content/uploads/2026/01/as16-pao.pdf) | 1,629 | 51,277,220 | `0D5B8F6DD220959044CD074F9CC43898184EEB32850BABD963580D162CE5ACB8` |

The timed file was RFC 9110. The Apollo 11 report was the other open document. Save them as `rfc9110.pdf` and `apollo11-mission-report.pdf` under `artifacts/reader-baseline/corpus`. Run `python eng/Measure-ReaderBrowserPilot.py` in an interactive Windows session with Python 3.13.15, Pillow 13.0.0 and NumPy 2.5.4. The script uses dedicated guest profiles and alternates Chrome and Edge order. It starts timing before dispatching the file to the browser and samples the browser window via `PrintWindow`. A trial ends when a document crop containing first-page text matches the manually inspected settled reference. Sample intervals are recorded alongside elapsed time. The crop excludes browser chrome, thumbnails and blank margins. This measures a captured window frame with text, with the sampling delay included.

Cold-process opens start a new guest browser process after warmup, with Windows' file cache left warm. Resident opens dispatch the local RFC URL to the browser executable while the Apollo report is open. Warm reopens first open the RFC, close its tab, then reopen it in the same browser process. All three use the browser's default PDF layout. The five samples per cell establish a pilot only. At this sample count, nearest-rank p95 is the slowest observed result. [Raw observations](evidence/reader-browser-pilot-2026-11-23.csv)

| Condition | Chrome 156.0.8078.82 p50 / p95 | Edge 159.0.4402.13 p50 / p95 |
| --- | ---: | ---: |
| Cold process | 1,216 / 1,243 ms | 1,220 / 1,343 ms |
| Warm reopen | 640 / 859 ms | 443 / 585 ms |
| Resident with another PDF | 859 / 870 ms | 491 / 529 ms |

The `363243e` x64 Debug comparison build used .NET 11.0.100 and Windows App SDK 2.1.3, with self-contained deployment and no package identity. Ctrl+O opened the picker and the RFC rendered fit-width. Its labeled welcome screen and compact top-level navigation were clear. The floating lower control pill covered document text.

The `1ad1004` x64 NativeAOT build used the pinned RC 3 SDK and Windows App SDK 2.6.6 experimental. Its unsigned MSIX passed static inspection (276 entries, SHA-256 `745CC0170B63B8B8391C6148732E014A935C40BC1D2A00DA8AFA93C0C90C6DEC`) and ran under a separate loose development identity. The picker opened the RFC on that build. The tab row remained off the document surface. The lower toolbar still covered the page, and the page control showed `1` without a visible total in that window. Windows file dispatch passed the RFC path to the executable on a cold launch. After eight seconds, the reader was showing a different, restored PDF. This trial failed the readable-page criterion. The first product slice corrects file activation before further redesign.

The RC 3 x64 release-mode worker replay completed 1,024 cases with zero crashes, zero deadlines and one unexpected `Win32Exception` on case `pdf-0685`. Its generic HRESULT was `0x80004005`. The process-name leak count was one while a separate ElliePdf test app had launched, so that count is inconclusive. The local report SHA-256 is `31C3D7E84B2636E6A01E8DD96968229AC171AFDC9B317F4550D70565C0957436`. The follow-up adds the native error code and attributes the leak count to the fuzz worker image.

## File-open fix and ElliePdf timing pilot

The stable .NET 11 x64 candidate built and passed static inspection (276 entries, SHA-256 `54B8578AD9691313C55A32B9807C5B37DFB980C6B8096F2E500BDEBF05154D1D`). Its loose package opened `RFC 9110 shell open.pdf` on cold Windows dispatch. Dispatching the Apollo 16 scan next opened a new tab in the same process with its existing tabs intact. Page 800 rendered readable transcript text after entry in the page field.

The local ElliePdf pilot dispatched RFC 9110 via `ShellExecuteEx` using the development package's PDF ProgID. The resident condition started the Apollo 11 report in the same viewer before dispatching the RFC. The OS file cache was warm, and cold opens restored the same three saved fixture tabs. The capture matched the title and first body paragraph against a manually inspected image and allowed for a 24-pixel vertical offset between two observed layout positions. An initial fixed-position match missed that offset and its values are excluded. The successful pilot used a 956 × 720 window, approximately, at 100% scaling. Restarting the test process between trials kept the saved fixture session consistent. [Raw observations](evidence/reader-ellie-pilot-2026-11-23.csv)

| ElliePdf condition | Samples | p50 | p95 |
| --- | ---: | ---: | ---: |
| Cold process | 5 | 1,228 ms | 1,435 ms |
| Resident with another PDF | 5 | 865 ms | 1,167 ms |

Both ElliePdf pilot conditions missed the browser targets. Warm reopen and the long-document timing matrix are pending. The pilot uses different window widths for ElliePdf and the browsers, small sample sizes and separate test passes. Speed acceptance requires matched viewports and interleaving all three viewers.
