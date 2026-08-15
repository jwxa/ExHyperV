# Progress Log

## Context Recovery Block

- **Current milestone**: #4 - Run complete issue validation
- **Current status**: DONE
- **Last completed**: #4 - Run complete issue validation
- **Current artifact**: `.codex-tasks/remote-host-management/tasks/issue-03-host-profiles/TODO.csv`
- **Key context**: Preserve the existing current-Windows-identity path. Profiles may reference an explicit credential target, but configuration must never contain a password.
- **Known issues**: Full WPF build requires the pre-existing COM tool `AxImp.exe`; shared services remain executable through the standalone test project.
- **Next action**: Consume the proven profile boundary through the active-host session coordinator; no further Issue #3 work remains.

## Mapping Notes

- Existing preferences live at `%LOCALAPPDATA%\ExHyperV\Config.xml`; remote profiles will use a separate `Hosts.xml` in the same directory.
- `WmiContext.Local` already uses the current process identity, while remote contexts currently require username/password. Extending remote identity consumption belongs to the session coordinator, not profile persistence.
- The profile file will contain only profile ID, display name, IPv4 address, authentication mode, and a deterministic credential target reference.

## Completed Result

- Added strict IPv4 profile validation and a versioned, atomic, UTF-8 `Hosts.xml` store.
- Modeled current Windows identity plus remembered or transient explicit credentials without serializing passwords.
- Added a narrow Windows Credential Manager adapter and orchestration for profile save/edit/delete.
- Proved editing a remembered profile does not read or echo its password; deletion can clean the target and rolls back the profile file if cleanup fails.
- Validation: 15/15 executable tests pass, `git diff --check` passes, and all 21 new source/test files are UTF-8 without BOM.
- Completion audit reopened validation because malformed `Hosts.xml` currently leaks the platform XML parser message and no test proves malformed or unsupported versions remain untouched.
- Closed the audit gap by parsing with DTD disabled, mapping malformed XML to a stable Chinese `InvalidDataException`, and preserving the underlying `XmlException` for diagnostics.
- Added direct byte-preservation tests for malformed XML, unsupported format version 99, and adjacent `Config.xml`; the complete deterministic suite passes 141/141 and the Release product build has 0 errors.
