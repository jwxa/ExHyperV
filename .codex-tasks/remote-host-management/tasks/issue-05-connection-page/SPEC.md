# Issue #5 - Connection Page and Two-Channel Diagnostics

## Task Shape

- **Shape**: `single-full`

## Goals

- Implement the accepted layout B host-connection workspace in WPF-UI/Fluent style.
- Display saved profiles, local/active host identity, selected profile details, and independent management/console channel state.
- Run ordered IPv4 reachability, identity, WMI/DCOM namespace, and TCP 2179 checks with duration and detailed Chinese logs.
- Allow WMI success with TCP 2179 failure to produce a partially available result.

## Non-Goals

- Do not commit a selected remote host as active; atomic switching belongs to Issue #6.
- Do not migrate VM operations or the console to remote hosts.
- Do not modify remote host configuration; the guided configurator belongs to Issues #11 and #12.

## Deliverables

- Narrow diagnostics contracts and Windows adapters for WMI/DCOM and TCP 2179.
- Deterministic diagnostic pipeline and result/error model.
- WPF connection page, ViewModel, navigation registration, and add/edit/delete profile interactions.
- Executable tests for success, partial availability, failures, cancellation, ordering, and credential handling.

## Done-When

- [x] WMI/DCOM and TCP 2179 are separately visible and independently testable.
- [x] WMI success plus TCP 2179 failure is represented as partially available.
- [x] Diagnostic steps expose status, duration, Chinese explanation, and ordered detailed log lines.
- [x] Selecting a profile does not activate it.
- [x] Tests, full WPF build, and `git diff --check` pass.

## Final Validation Command

```powershell
dotnet run --project tests/ExHyperV.Tests/ExHyperV.Tests.csproj; dotnet build src/ExHyperV.csproj; git diff --check
```
