# Progress Log

## Context Recovery Block

- **Current milestone**: Complete
- **Current status**: DONE
- **Last completed**: Issue #5 connection page and two-channel diagnostics
- **Current artifact**: `.codex-tasks/remote-host-management/tasks/issue-06-atomic-switch/TODO.csv`
- **Key context**: Selection and diagnostics already exist. Switching must publish no intermediate active session and must not migrate VM operations before Issue #7.
- **Known issues**: Full default output is locked by an elevated verification instance; use the isolated output directory until it exits. Existing nullable/high-DPI and SSH.NET warnings remain out of scope.
- **Next action**: Continue with GitHub issue #7 remote VM list and lifecycle operations.

## Design Notes

- The coordinator is the sole atomic publication boundary.
- A candidate connection is disposable and remains private until its base snapshot succeeds.
- The write gate atomically checks zero active writes and freezes new writes for the transaction.
- Consumers capture an immutable operation stamp and ask the coordinator whether it still belongs to the active generation before applying asynchronous results.

## Session 2026-08-13

- Added the missing test assertion and validated all atomic switch contract scenarios: 37/37 executable tests pass.
- Added remote current-identity and explicit-credential WMI contexts with per-session cache identity, Windows connector and basic snapshot loader, and startup configuration.
- Validated 38/38 executable tests and a complete isolated WPF build with zero errors.
- Wired management-gated confirmation, switch progress/results, active identity/snapshot display, and explicit local return into layout B.
- Rendered and inspected 1100x800 and 720x900 layouts; adjusted the narrow host-strip row ratio to remove clipping.
- Added bounded WMI connection/query options and deferred cleanup for timed-out background connections.
- Prevented editing or deleting the profile backing the active remote session until the user explicitly returns local.
- Final validation: 38/38 tests, complete isolated WPF build with 0 errors, `git diff --check`, UTF-8 no-BOM verification, and wide/narrow pixel inspection all pass.

## 2026-08-14 completion audit

- Extended the write guard from remote VM lifecycle operations to local host settings, virtual switches, USB tunnels, PCIe assignment, VM creation/deletion, and every VM advanced-setting mutation module.
- Mutation entrypoints acquire the lease after user confirmation and release it through scoped disposal; multi-step operations keep one lease through required compensation.
- Disabled the saved-host selector while a switch is active so UI selection cannot diverge from the coordinator after returning to local.
- Deterministic validation passes 167/167 and the Release build has 0 errors.
