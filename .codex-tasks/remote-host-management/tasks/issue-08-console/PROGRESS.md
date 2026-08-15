# Progress Log

## Context Recovery Block

- **Current milestone**: Complete
- **Current status**: DONE
- **Last completed**: Issue #8 remote TCP 2179 console
- **Current artifact**: `.codex-tasks/remote-host-management/tasks/issue-08-console/TODO.csv`
- **Key context**: Console windows now consume an immutable active-host capture, target the captured IPv4 on TCP 2179, and bind status/lifecycle WMI work to the same generation.
- **Known issues**: Controlled-host proof is externally blocked because `10.0.0.6:2179` timed out after 3 seconds. GitHub API access is also unavailable, so issues #7 and #8 remain to be closed remotely. Existing nullable/high-DPI and SSH.NET warnings remain out of scope.
- **Next action**: Start Issue #9 disconnect, stale-data, and automatic reconnect behavior.

## Design Notes

- Keep `RdpClientHost` as the existing deep module; it remains unaware of Hyper-V host selection.
- Add one session-layer interface that captures target address, VM identity, host operation stamp, and console availability atomically.
- Treat the captured session as immutable. A host generation change invalidates the window instead of silently retargeting an open console.
- Reuse `ActiveHostVmOperations` for WMI state and supported lifecycle actions; do not duplicate local/remote branching in the window.
- Scope console window identity by host generation and VM id so equal VM GUIDs or names cannot cross host sessions.

## 2026-08-13 - Console Session Contract Complete

- Added atomic console operation capture to the active-host coordinator.
- Captures reject unavailable TCP 2179 or stale host data with a Chinese reason.
- Local targets map to `localhost:2179`; remote targets preserve the active IPv4 address.
- Captured sessions carry an immutable host operation stamp and a host-generation-scoped window key.
- Validation: 51/51 tests passed.

## 2026-08-13 - Issue Complete

- Routed `ConsoleWindow` and the Hyper-V RDP recipe through the captured host address and TCP 2179.
- Scoped window deduplication by host generation, profile, and VM GUID; old-generation windows close when host state invalidates the capture.
- Bound console polling and Start, Stop, TurnOff, and Restart to the captured WMI context and generation.
- Kept pause, save state, and CAD local-only; no unsupported local resources are exposed remotely.
- Added early and post-initialization generation checks; the latter tears down the initialized RDP ActiveX host before rejecting a stale window.
- Validation: 54/54 tests; Release WPF build 0 errors and 248 existing warnings; 94 changed source/document files are UTF-8 without BOM; 5 changed XAML files parse; `git diff --check` passes.
- Controlled-host evidence: read-only TCP connect to `10.0.0.6:2179` timed out after 3 seconds, so no successful remote console claim is made.
- Remote tracking: `api.github.com` remained unreachable; GitHub issues #7 and #8 still require closure.
