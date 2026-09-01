# 0004: PDFium annotation persistence and explicit flattening

## Status

Accepted for the opt-in Labs editing surface.

## Context

WP-12 requires normal Save to preserve the document structure while persisting ink, text stamps,
and image-signature stamps. It also requires flattening to remain an explicit Save Copy operation.
The writer must run inside the existing sandboxed worker and must not weaken the atomic-save,
recovery, telemetry, or file-handle boundaries. Adding a second PDF writer would introduce another
native parser, another provenance and patch stream, and a second set of preservation semantics.

The PDFium annotation and page-object APIs are preview-sensitive. A cancellation experiment also
showed that deleting newly authored annotation appearance objects and then importing pages can
terminate the pinned PDFium build. Native rollback is therefore not a safe transaction primitive.

## Decision

ElliePdf uses the same pinned and verified PDFium build for reading and Labs writing. The worker
creates native Ink annotations with an InkList plus a stroked appearance path. Text and signature
stamps use native Stamp annotations with text or image appearance objects. Every ElliePdf-created
annotation receives a deterministic, bounded `/NM` identifier derived from the edit identity and
content. Repeating the same request is idempotent.

Normal annotation Save is a broker-controlled two-phase operation:

1. The worker validates the complete bounded request, authors annotations in its isolated document,
   and writes a candidate to the broker-owned temporary handle.
2. The broker validates and atomically commits the candidate.
3. A successful source-file commit advances the worker's saved revision. A failed commit or a Save
   As destination leaves the staged annotations as unsaved worker state. A retry reuses the stable
   identifiers and does not duplicate them; closing or discarding the isolated session drops them.

This intentionally avoids deleting freshly authored native appearance objects during routine abort.
If staging fails after native mutation begins, the worker reports a transient restart-required error
and exits; discarding the isolated process is the rollback boundary.
The source path is never sent to the worker and is not modified before broker commit. Recovery
reconciliation subtracts only the exact captured edits, so edits made while a save is in flight remain
dirty.

Save Flattened Copy always targets a different destination. The worker clones the document, flattens
pre-existing annotations on every page in the clone even when there are no current ElliePdf overlays,
inserts requested ElliePdf overlays as direct page content,
generates page content, and writes the clone. It never mutates the reader session or source document.
Normal Save does not rasterize pages: selectable text, links, outlines, forms, metadata, permissions,
page boxes, and rotations remain document structures.

The additive writer shapes advance the wire-neutral PDF contract to version 1.1 while the authenticated
transport envelope remains version 1.0. All requests enforce contract bounds for page count, transaction size, coordinates, stroke points,
text, decoded signature dimensions and bytes, colors, and identifiers. Annotation permission checks
are enforced before mutation. Paths, annotation content, text, and signature bytes remain excluded
from telemetry. The native release gate verifies every PDFium import for x64 and ARM64 in addition to
the pinned DLL hash, length, and PE machine type.

## Consequences

There is one native dependency and one preservation model to test and service. Atomic replacement,
external-change detection, recovery, AppContainer isolation, and broker-owned handles remain shared
with every other save path. Save As and commit failure can consume worker memory until retry, discard,
or close, but they do not alter the source and avoid the verified unsafe native undo sequence.

The Labs writer is disabled if a preview SDK or PDFium update removes an imported API, changes the
verified binary, fails NativeAOT, breaks sandbox execution, violates bounded-request tests, or regresses
the round-trip preservation suite. The production reader remains available because annotation writing
is an optional worker capability behind the Labs gate.

## Verification

The contract, transport, recovery, worker, and real-client suites cover request bounds, permissions,
transaction authority, abort/retry idempotence, exact captured-edit reconciliation, editable native
annotation output, structure preservation, explicit flattening, source immutability, and execution
through the actual sandboxed worker. Release verification additionally runs the PDFium export/hash
gate, x64 NativeAOT publish, ARM64 build, and package lifecycle checks.
