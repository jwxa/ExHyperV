# Progress Log

## Context Recovery Block

- **Current milestone**: #4 - Run complete issue validation
- **Current status**: DONE
- **Last completed**: Issue #9 disconnect, stale data, and reconnect
- **Current artifact**: `.codex-tasks/remote-host-management/tasks/issue-09-reconnect/TODO.csv`
- **Key context**: The coordinator already owns immutable active-session snapshots, active candidates, generation stamps, and write freezing. Reconnect must extend that fact source instead of placing a state machine in the ViewModel.
- **Known issues**: Controlled host `10.0.0.6` remains unreachable on TCP 135 and 2179, so live-host recovery proof is externally blocked. Existing nullable/high-DPI and SSH.NET advisory warnings remain out of scope.
- **Next action**: Start Issue #10 capability matrix and feature gating.

## Design Notes

- Keep the active remote target and generation stable while reconnecting; a successful reconnect publishes a new generation only after both connection and snapshot preparation complete.
- Store no new credentials. Reconnect reuses the in-memory switch request already required for the active session; it is cleared when leaving that host.
- Treat the coordinator as the only reconnect owner. Callers report a loss and observe state; they do not schedule retries.
- Use an injected delay adapter so retry timing and cancellation are deterministic in tests.
- Keep backoff constants conservative and bounded; no jitter is required for a single active host in the first version.

## Completion Summary

- Added immutable reconnect state and an injectable scheduler with capped delays of 2, 4, 8, 16, and 30 seconds.
- The active-host coordinator retains the last snapshot as stale, rejects new writes/console captures, waits for active writes, and owns one cancellable reconnect loop.
- Successful reconnect rebuilds WMI, rechecks TCP 2179 independently, refreshes the host snapshot, disposes the old candidate, and publishes a new generation without silently returning to local.
- Selecting another saved profile without activating it leaves the original active host reconnecting; an explicit switch cancels and waits for the old reconnect task.
- Connection and VM pages show stale/reconnect state, stop/retry commands, and disabled remote controls with explanations.
- Fixed reconnect task registration/cleanup ordering and increased the narrow host-strip height so the full stale host snapshot is visible.

## Validation

- `65/65` combined tests pass.
- Isolated WPF Release build passes with `0` errors and `248` existing warnings.
- `31/31` XAML files parse; `81` scoped source/task files are UTF-8 without BOM; `git diff --check` passes.
- Wide `1100x800` and narrow `704x861` reconnect-state screenshots pass pixel inspection.
- Controlled host probes fail for both `10.0.0.6:135` and `10.0.0.6:2179`; live proof remains an explicit external blocker.

## 2026-08-14 completion audit

- Wired unexpected remote RDP disconnect and fatal-error events into `ReportConnectionLoss` using the captured host generation.
- Before marking the host stale, the console performs a generation-bound WMI check and ignores normal VM stop/delete disconnects; WMI loss still enters the same reconnect owner through the active-host operation path.
- Added deterministic source contracts for both disconnect event paths. The suite passes 167/167 and the Release build has 0 errors.
