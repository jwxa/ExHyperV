# Progress Log

## Context Recovery Block

- **Current milestone**: #4 - Run complete issue validation
- **Current status**: DONE
- **Last completed**: Issue #9 disconnect, stale data, and reconnect
- **Current artifact**: `.codex-tasks/remote-host-management/tasks/issue-10-capabilities/TODO.csv`
- **Key context**: The coordinator already publishes immutable active-session, channel, stale, reconnect, and switch/write-freeze state. Capability facts must extend that snapshot and replace duplicated local/remote/channel inference in pages.
- **Known issues**: Controlled host `10.0.0.6` is unavailable, so deterministic snapshots and UI rendering provide acceptance evidence. Existing warnings remain out of scope.
- **Next action**: Start Issue #11 Chinese read-only configuration preflight wizard.

## Design Notes

- Model capability as a state plus a stable reason code and Chinese explanation; avoid a separate Boolean for every page action.
- Keep the first matrix finite and explicit: VM read, VM write, console, local host hardware, local VM advanced settings, local file/device access, virtual switch, PCIe, and USB forwarding.
- Recompute centrally whenever the coordinator publishes a snapshot, including temporary switch and stale states.
- UI adapters may expose convenience properties, but they must consume capability entries rather than re-derive host/channel state.

## Session 2026-08-13

- Added a finite immutable capability matrix with channel, stale-data, switching, and remote-not-supported reason codes.
- Fixed matrix value equality and atomic switch publication so callers never observe a new host with temporary switch gates.
- Restored all capability explanations to readable UTF-8 Chinese.
- Updated session event tests for one explicit switching snapshot while retaining generation and immutability checks; 70/70 tests pass.
- Routed coordinator read/write/console guards, VM page commands, console commands, main navigation, and cached local-only pages through the same capability matrix.
- Local-only Host, PCIe, virtual-switch, and USB pages no longer preload or continue local hardware work while their capability is unavailable; USB background loops stop and resume with capability changes.
- Added matrix value-equality and backend non-invocation tests; final automated result is 72/72 tests, Release WPF build 0 errors/248 existing warnings, 85 changed text files UTF-8 without BOM, all XAML parses, and `git diff --check` passes.
- Native WPF visual inspection was started through Computer Use but stopped by the user's physical Escape key. No further Computer Use calls were made; wide/narrow visual evidence remains the only pending gate.
- Extended the offline real-WPF renderer to construct `MainWindow` against a deterministic remote active-host session. Wide (1100x800) and narrow (720x900) screenshots show no overlap, and text evidence confirms VM remains enabled while Host, PCIe, Switch, and USB remain visible, disabled, and expose the expected Chinese reasons.
- Validation evidence: `raw/issue-10-capabilities-wide.png`, `raw/issue-10-capabilities-narrow.png`, and their matching `.txt` files.
