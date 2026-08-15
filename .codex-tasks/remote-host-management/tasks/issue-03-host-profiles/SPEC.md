# Issue #3 - Saved Host Profiles and Windows Credentials

## Task Shape

- **Shape**: `single-full`

## Goals

- Persist multiple IPv4 Hyper-V host profiles without storing passwords in project configuration.
- Keep current Windows identity as the default authentication mode.
- Allow an optional explicit Windows credential reference stored in Windows Credential Manager.
- Provide deterministic profile validation and storage tests for later connection UI work.

## Non-Goals

- Do not build the connection page in this ticket.
- Do not connect to WMI/DCOM or TCP 2179.
- Do not add credential prompting or host switching.

## Constraints

- Reuse `AppDataPaths` and existing settings conventions where practical.
- UTF-8 without BOM and atomic configuration replacement.
- Store only credential target identifiers in profile files; never store passwords.
- Treat current Windows identity as a built-in mode that needs no credential decision.

## Deliverables

- Host profile domain model, validation, and persistence service.
- Windows Credential Manager abstraction and Windows implementation.
- Executable tests for multiple profiles, validation, atomic persistence, and secret-free files.

## Done-When

- [ ] Profile and credential tests pass.
- [ ] No password is serialized to the profile store or logs.
- [ ] `git diff --check` passes.

## Final Validation Command

```powershell
dotnet run --project tests/ExHyperV.Tests/ExHyperV.Tests.csproj; git diff --check
```
