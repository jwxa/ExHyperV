# Progress Log

## Context Recovery Block

- **Current milestone**: #3 - Run complete issue validation
- **Current status**: DONE
- **Last completed**: #3 - Run complete issue validation
- **Current artifact**: `.codex-tasks/remote-host-management/tasks/issue-04-session-coordinator/TODO.csv`
- **Key context**: Profiles are persisted by Issue #3. The coordinator must start local every time and keep selected profile separate from active session.
- **Known issues**: Full WPF build requires the pre-existing COM tool `AxImp.exe`; coordinator behavior can be compiled and executed through the standalone test project.
- **Next action**: Use the coordinator as the single source of truth when implementing the connection page and atomic switching in Issues #5 and #6.

## Completed Result

- Added immutable target, active-session, channel-state, connection-state, and coordinator snapshot contracts.
- Added a thread-safe coordinator that starts local, keeps profile selection separate from activation, and publishes coherent snapshots outside its lock.
- Added application startup wiring for the process-wide local coordinator.
- Enforced monotonic session generations and selected-profile matching for internal remote commits.
- Validation: 19/19 combined tests and `git diff --check` pass.
