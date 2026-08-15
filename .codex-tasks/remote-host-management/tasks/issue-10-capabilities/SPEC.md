# Issue #10 - Capability Matrix and Feature Gating

## Task Shape

- **Shape**: `single-full`

## Goals

- Publish one immutable capability snapshot from the active-host coordinator.
- Distinguish available, read-only, unavailable-channel, stale-data, switching, and remote-not-supported reasons.
- Drive VM management, TCP 2179 console, local hardware/device features, and main navigation from the same matrix.
- Keep unsupported entries visible and disabled with specific Chinese explanations.
- Enforce the same gates inside commands so keyboard or direct invocation cannot bypass UI state.

## Non-Goals

- Do not add remote implementations for host hardware, PCIe, virtual switch, USB, HCS, registry, or local filesystem features.
- Do not redesign the connection protocols or reconnect state machine.
- Do not implement the configuration wizard from Issues #11 and #12.

## Deliverables

- Immutable capability contracts and reason codes in the remote session module.
- Capability recomputation for local, remote, partial, stale, reconnecting, and switching states.
- Shared helpers/adapters for ViewModel and navigation gating.
- Tests for local full capability, remote management, partial availability, stale data, switching, and remote unsupported features.
- UI verification that disabled entries remain visible and explain why.

## Done-When

- [ ] The coordinator snapshot is the only source of host/channel capability decisions.
- [ ] WMI VM management and TCP 2179 console capabilities are independent.
- [ ] Remote-unsupported local hardware/device/file features remain visible and disabled.
- [ ] Every disabled state has a specific Chinese reason.
- [ ] Loaded pages and main navigation react to one capability snapshot after switch, loss, and recovery.
- [ ] Direct command invocation cannot bypass capability gates.
- [ ] Tests, WPF build, UTF-8, XAML, visual checks, and `git diff --check` pass.

## Final Validation Command

```powershell
dotnet run --project tests/ExHyperV.Tests/ExHyperV.Tests.csproj; dotnet build src/ExHyperV.csproj; git diff --check
```
