# Issue #2 - Rolling Logs and Redaction

## Task Shape

- **Shape**: `single-full`

## Goals

- Write detailed UTF-8 Chinese-capable logs under `<ExHyperV.exe directory>\logs`.
- Rotate `ExHyperV.log` at 100 MB to one `ExHyperV.1.log`; keep at most about 200 MB.
- Centralize structured context and sensitive-data redaction for later remote-host modules.
- Keep the application running and expose a user-readable state when logging cannot initialize or write.

## Non-Goals

- Do not replace every existing `Debug.WriteLine` in this ticket.
- Do not add remote-host connection behavior.
- Do not add a third-party logging framework.

## Constraints

- .NET 8 / Windows / WPF compatible.
- UTF-8 without BOM.
- Thread-safe writes and deterministic tests using a small configured size limit.
- No password, token, authorization value, credential object, or secret property may reach disk.

## Deliverables

- Rolling file logger and process-wide facade.
- Startup/shutdown integration and user-visible unavailable notification.
- Automated tests for path, encoding, rotation, restart append, concurrency, and redaction.

## Done-When

- [ ] Every acceptance criterion in GitHub issue #2 is proven.
- [ ] Main project builds.
- [ ] Logging tests pass.
- [ ] `git diff --check` passes.

## Final Validation Command

```powershell
dotnet test tests/ExHyperV.Tests/ExHyperV.Tests.csproj; dotnet build src/ExHyperV.csproj --no-restore
```
