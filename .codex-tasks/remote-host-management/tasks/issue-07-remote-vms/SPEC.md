# Issue #7 - Remote VM List and Lifecycle

## Task Shape

- **Shape**: `single-full`

## Goals

- Load the VM list and runtime state from the process-wide active host WMI context.
- Preserve current local-host list and lifecycle behavior.
- Support remote start, graceful shutdown, forced power-off, and restart.
- Bind every read result and write operation to an immutable active-host generation.
- Show the active host and VM name in dangerous confirmations; show target count for batches.
- Clear old-host selection and reject late old-generation results after a host switch.

## Non-Goals

- Do not make local filesystem, registry, HCS, hardware, GPU, PCIe, USB, screenshot, or device-enumeration features remote.
- Do not implement TCP 2179 console support; that belongs to Issue #8.
- Do not implement reconnect scheduling; that belongs to Issue #9.

## Deliverables

- Active WMI context access contract backed by the active host candidate.
- Generation-bound VM list query facade and lifecycle executor.
- VM page host-change clearing/reload behavior and stale-result rejection.
- Host-aware confirmations and remote-safe lifecycle UI behavior.
- Executable tests for local/remote context selection, lifecycle success/failure, write gate, and stale completions.

## Done-When

- [ ] VM list queries use the active host context and local behavior remains compatible.
- [ ] Remote start, graceful shutdown, forced power-off, and restart return explainable results.
- [ ] Every write uses a write lease and late old-generation results cannot update the new host UI.
- [ ] Dangerous confirmations identify active host and VM, or the batch target count.
- [ ] Host changes clear old list/selection before reloading the new host.
- [ ] Local-only commands are not presented as remote-capable.
- [ ] Tests, full WPF build, controlled-host verification or an explicit external blocker, and `git diff --check` pass.

## Final Validation Command

```powershell
dotnet run --project tests/ExHyperV.Tests/ExHyperV.Tests.csproj; dotnet build src/ExHyperV.csproj; git diff --check
```
