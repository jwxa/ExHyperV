# Progress Log

## Context Recovery Block

- **Current milestone**: Complete
- **Current status**: DONE
- **Last completed**: #4 - Run complete issue validation
- **Current artifact**: `.codex-tasks/remote-host-management/tasks/issue-07-remote-vms/TODO.csv`
- **Key context**: VM list reads and the four supported lifecycle writes now use the captured active-host context and generation; local-only operations remain explicitly gated on remote hosts.
- **Known issues**: Controlled-host verification against `10.0.0.6` is externally blocked: TCP 135 and 2179 are unreachable and WMI reports `RPC server unavailable`. Existing nullable/high-DPI and SSH.NET warnings remain out of scope.
- **Next action**: Begin Issue #8 remote TCP 2179 console support.

## Design Notes

- A single session-layer resolver maps the active coordinator snapshot to its WMI context; service code does not inspect credentials or candidates.
- Reads capture an operation stamp and apply results only if the stamp still matches.
- Writes acquire a coordinator lease before issuing WMI and apply results only while the lease stamp remains current.
- Forced remote power-off must never fall back to killing a local `vmwp.exe` process.

## 2026-08-14 - Operation Contract Complete

- Added atomic `HostManagementOperationContext` capture without exposing the active candidate in public snapshots.
- Added a WMI context resolver and generation-bound read/write executor.
- Added six tests covering local/remote context selection, stale reads, write lease switching protection, frozen-write rejection, and backend failures.
- Validation: 44/44 tests passed.

## 2026-08-13 - Issue Complete

- Migrated VM list and lifecycle services to explicit local/remote `WmiContext` selection.
- Kept full local VM aggregation unchanged; remote list aggregation reads only WMI-backed summary, memory, and system settings.
- Added remote-safe start, graceful shutdown, forced power-off, and restart without local process fallbacks.
- Bound reads and writes to immutable host generations; every write acquires an `IHostWriteLease` and rejects a confirmed operation after the active host changes.
- Updated the VM page to cancel and clear stale list, selection, thumbnails, and multi-selection before loading a new host.
- Added a remote VM detail template exposing only the four lifecycle actions; console, create, rename, delete, files, GPU, PCIe, and advanced hardware remain disabled with explanations.
- Host-aware confirmations identify the active host and VM name; batch shutdown identifies the active host and target count.
- Final validation: 46/46 tests passed; isolated WPF build completed with 0 errors and 249 existing/current warnings; XAML XML, strict UTF-8 without BOM, and `git diff --check` passed.
- UI smoke check: the isolated elevated build launched and rendered the main window correctly. Desktop automation could capture it but could not inject navigation into the elevated process, so the remote template was verified through successful WPF compilation and static XAML inspection rather than a fabricated remote session.
- Controlled-host verification blocker: current Windows identity WMI query to `10.0.0.6` returned `RPC server unavailable`; TCP 135 and 2179 were unreachable. No VM write operation was attempted.
