# Issue #6 - Atomic Host Switching and Write Guard

## Task Shape

- **Shape**: `single-full`

## Goals

- Switch a confirmed selected profile into the process-wide active host without exposing a partially prepared session.
- Block switching while writes are active and reject new writes while a switch owns the freeze lease.
- Load a basic host snapshot before one atomic publication of the new session generation.
- Preserve the old active host and snapshot on preparation, connection, snapshot, cancellation, or stale-selection failure.
- Let the user explicitly return to the local host; never silently fall back to local.

## Non-Goals

- Do not migrate VM list or lifecycle calls to the active WMI context; that belongs to Issue #7.
- Do not implement disconnect/reconnect scheduling; that belongs to Issue #9.
- Do not expand remote support for local hardware features.

## Deliverables

- Session candidate, basic snapshot, operation stamp, write lease, and switch result contracts.
- Coordinator orchestration with injected connector/snapshot loader and atomic state publication.
- Windows WMI context adapter supporting both current Windows identity and explicit credentials.
- Connection-page confirmation, progress, active identity/status display, remote switch, and explicit local return.
- Executable tests for success, connection/snapshot failure, write blocking/freeze, stale completion, cancellation, and local return.

## Done-When

- [ ] Confirmation names the target host, IPv4 address, and identity mode.
- [ ] Active writes prevent switching and a switching freeze rejects new writes.
- [ ] Candidate connection and base snapshot are complete before a single new generation is published.
- [ ] Every failure and cancellation path retains the old active host and snapshot.
- [ ] Operation stamps from the old generation cannot apply after a successful switch.
- [ ] The user can explicitly return to local without any automatic fallback.
- [ ] Tests, full WPF build, pixel inspection, and `git diff --check` pass.

## Final Validation Command

```powershell
dotnet run --project tests/ExHyperV.Tests/ExHyperV.Tests.csproj; dotnet build src/ExHyperV.csproj; git diff --check
```
