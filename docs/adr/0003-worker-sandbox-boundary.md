# 0003: Worker sandbox boundary and compatible launch modes

## Status

Accepted.

## Context

PDF parsing and rendering process attacker-controlled bytes. The UI process must not load the
native parser, and a failure in the worker must be bounded even when the worker is compromised.

## Decision

Every worker is placed in one Windows Job Object with a 512 MiB default job-memory cap, CPU hard
cap, kill-on-client-exit, unhandled-exception termination, and an active-process limit of one.
The Job also denies desktop, clipboard, global-atom, display, system-parameter, window-exit, and
cross-process UI-handle interactions. Document data is supplied only by duplicated read/write
handles; the worker never receives a UI-originated path.

The broker first creates the worker suspended in a persistent, capability-free AppContainer
profile (`ElliePdf.PdfWorker.NoCapabilities.v1`). The profile receives read/execute access only to
the app-private, self-contained worker payload. It receives no filesystem, network, or other
capability SID. Less Privileged AppContainer is an explicit opt-in that additionally removes the
broad `ALL APPLICATION PACKAGES` group. The Job is fully configured and the child is assigned
before its initial thread is resumed.

The full-trust broker owns the per-launch named-pipe server. Its protected DACL grants access only
to the current interactive user and the dedicated worker AppContainer SID. A random pipe name is
not treated as authentication: every envelope is authenticated with a 256-bit, per-launch secret
delivered through an inherited anonymous-pipe handle. PDF inputs and merge outputs cross the
boundary only as least-access handles duplicated into the already-contained worker. UI-originated
paths are never serialized to the worker protocol.

The worker creates render mappings in its private AppContainer object namespace. The broker opens
them through the host-visible path returned by `GetAppContainerNamedObjectPath`, validates the
session-bound lease metadata, acknowledges acquisition, and releases each lease exactly once.
An ambient mapping with the same ordinary `Local\\` naming scheme is not visible to the worker.

`PdfWorkerClient.ActiveSandboxMode` reports `LessPrivilegedAppContainer` or `AppContainer`. Release
defaults set `RequireAppContainerSandbox=true` and use the fully exercised capability-free regular
AppContainer mode; an AppContainer/profile/ACL launch failure therefore fails closed. LPAC remains
an explicit package opt-in after installer ACL validation. Debug builds may make an explicit,
observable fallback to `RestrictedToken`, and only hosts that also opt out of the restricted-token
requirement may reach `JobConstrainedCompatibility`. Neither fallback is silently described as an
AppContainer.

Installed payload ACLs are installer-owned. If inherited read/execute access is already present for
the worker SID (or, in regular AppContainer mode, `ALL APPLICATION PACKAGES`), startup performs no
ACL mutation. If a protected Program Files payload lacks that grant, startup fails closed with an
installer-actionable error. Runtime ACL provisioning is limited to user-writable development
outputs.

## Network boundary

The production AppContainer token has zero capability SIDs, including no `internetClient`,
`privateNetworkClientServer`, or loopback exemption. The executable negative boundary test launches
the real self-contained worker in both AppContainer and LPAC modes and verifies that it cannot read
or create an arbitrary file, open a broker-owned ambient mapping, or connect to a listening
loopback socket. The same test verifies `TokenIsAppContainer=1` and an empty `TokenCapabilities`
group list. This test is a mandatory release gate; restricted-token compatibility modes do not
claim the same network guarantee.

## Consequences

The worker cannot launch helpers, keep running after the broker releases the Job, access desktop
interaction surfaces, traverse arbitrary user files, or initiate network traffic. The x64 and
ARM64 worker payloads must remain self-contained so LPAC startup does not depend on machine-wide
.NET runtime or registry access. The release gate consists of the real worker round-trip tests,
the executable sandbox-denial tests, and the existing authenticated-transport and worker protocol
test suites. No password, path, or socket capability is added to the IPC protocol.
