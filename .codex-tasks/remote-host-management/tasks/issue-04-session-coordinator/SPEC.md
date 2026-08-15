# Issue #4 - Active Host Session Coordinator

## Task Shape

- **Shape**: `single-full`

## Goals

- Establish one process-wide source of truth for the selected profile and active host session.
- Always create a local active session at application startup, regardless of saved remote profiles.
- Publish immutable, generation-tagged snapshots suitable for later connection, switching, stale-data, and write-guard work.
- Keep selection separate from activation so inspecting a profile cannot switch the management target.

## Non-Goals

- Do not connect WMI/DCOM or TCP 2179.
- Do not implement the atomic remote switching transaction or write-operation guard from Issue #6.
- Do not implement diagnostics, reconnect scheduling, capability calculation, or UI.

## Deliverables

- Immutable host/session state types.
- Thread-safe active-host session coordinator with local startup and independent profile selection.
- Executable behavior tests for startup, selection, generation, snapshot immutability, and event publication.

## Done-When

- [ ] Startup is always local even when remote profiles exist.
- [ ] Selecting a profile does not change the active session or generation.
- [ ] Coordinator snapshots are immutable and events publish coherent state.
- [ ] Tests and `git diff --check` pass.

## Final Validation Command

```powershell
dotnet run --project tests/ExHyperV.Tests/ExHyperV.Tests.csproj; git diff --check
```
