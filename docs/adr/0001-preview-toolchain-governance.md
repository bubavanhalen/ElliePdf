# ADR 0001: Pin the preview toolchain

## Status

Accepted

## Decision

ElliePdf uses the exact .NET SDK and package versions committed in the repository.
The SDK is pinned in `global.json` with prerelease enabled and roll-forward
disabled. Package versions are centralized and restore lock files are committed.
Only the scheduled preview-compatibility workflow may propose a toolchain update.
It discovers the newest .NET 11 prerelease SDK and the newest matching .NET,
Windows SDK and Windows App SDK prerelease packages from their official feeds,
regenerates locks, runs the complete dual-architecture publish gate, and opens or
refreshes an isolated review branch. It never promotes the candidate directly.

The workflow records a toolchain fingerprint and must pass restore, build, tests,
publish, and packaging checks before a new pin is promoted. The current source,
lock files, and unsigned payload are retained as the last-known-good rollback
baseline.

Known upstream warnings may be temporarily listed in
`eng/upstream-warning-allowlist.json`, with an owner, issue URL, and expiry date;
project warnings remain errors.

## Preview scope and compatibility

The preview policy covers every runtime and build-time dependency, including
NativeAOT toolchains, Windows SDK BuildTools, Windows App SDK, and .NET preview
libraries. Windows App SDK is intentionally consumed from the experimental
channel while this product is preview software; its exact package version, feed,
and transitive lock entries are reviewed as one atomic candidate. No preview
package may be upgraded ad hoc in an application project.

NativeAOT is a release requirement for both x64 and ARM64 application and PDF
worker. A candidate is not promotable until both architectures complete restore,
trimmed/AOT publish, protocol tests, package inspection, and smoke checks. If an
upstream preview breaks AOT or one architecture, the candidate is rejected and
the last-known-good pin remains supported. A framework-dependent Debug build is
for developer diagnosis only, never a production fallback.

## Cadence, promotion, and rollback

The scheduled preview workflow runs weekly and may also be dispatched for a
security or compatibility incident. It opens a review branch with updated pins,
lock files, a toolchain fingerprint, and benchmark comparison. The candidate
requires two consecutive green runs, including the complete dual-architecture
publish gate, before promotion; it never mutates `master` or a release tag
automatically.

`eng/last-known-good.json`, committed lock files, and the latest unsigned
dual-architecture payload form the rollback record. Rollback reverts all pins
and locks to that single set, rebuilds and re-signs both architectures, and
reruns package, protocol, and UI gates. The release lane must not mix a prior
runtime with a newer Windows App SDK or NativeAOT payload. Store rollback and
certificate rotation remain a separate forward-versioned release procedure.

## Feature-specific fallback

Preview-dependent features fail closed independently. If a preview dependency
breaks annotations, organization, or another Labs capability, that capability
is disabled behind its existing Labs gate while opening, navigation, search,
rendering, printing, and ordinary read-mode operations remain usable. If
rendering or AOT compatibility is affected, the release is blocked; there is no
silent substitution of a different PDF engine or runtime. Any temporary shim
must be isolated, telemetry-free by default, covered by a contract test, assigned
an owner, and carry an expiry/removal issue.
