# Issue #8 - Remote TCP 2179 Console

## Task Shape

- **Shape**: `single-full`

## Goals

- Open the existing Hyper-V console against the immutable active-host IPv4 address instead of always using `localhost`.
- Permit a console only when the captured active session reports TCP 2179 available.
- Bind each console window to the captured host generation and disconnect it when the active host changes.
- Keep VM state polling and the four supported lifecycle actions on the same captured host context.
- Preserve current local console behavior and reuse the existing `RdpClientHost` implementation.

## Non-Goals

- Do not implement reconnect scheduling or stale-data recovery; that belongs to Issue #9.
- Do not introduce a new RDP/VMConnect implementation or custom protocol.
- Do not transmit saved WMI passwords into MSTSCAX or add credential delegation.
- Do not enable remote pause, save-state, console-support mutation, files, devices, or advanced hardware operations.
- Do not build the full capability matrix; that belongs to Issue #10.

## Deliverables

- Immutable active-host console session capture contract.
- Generation-aware console target and availability validation.
- Navigation deduplication scoped by host generation plus VM identifier.
- Console ViewModel polling and lifecycle operations bound to the captured host context.
- RDP connection recipe using the captured active-host address and TCP 2179.
- Tests for local/remote targets, unavailable channel, stale generation, and host-scoped window identity.

## Done-When

- [ ] Local consoles still target `localhost:2179`.
- [ ] Remote consoles target the active host IPv4 on TCP 2179.
- [ ] A failed TCP 2179 diagnostic disables opening the console with an explainable reason.
- [ ] Host switching invalidates and disconnects old-generation console windows.
- [ ] Remote console status and lifecycle calls never fall back to local WMI or local process operations.
- [ ] Console window deduplication cannot reuse a VM window from another host generation.
- [ ] Tests, full WPF build, controlled-host verification or an explicit external blocker, UTF-8 checks, and `git diff --check` pass.

## Final Validation Command

```powershell
dotnet run --project tests/ExHyperV.Tests/ExHyperV.Tests.csproj; dotnet build src/ExHyperV.csproj; git diff --check
```
