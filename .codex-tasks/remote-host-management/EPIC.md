# Remote Host Management Epic

## Goal

- Implement the approved LAN Hyper-V host management specification in GitHub issue #1 through implementation issues #2-#13.

## Non-Goals

- Do not add IPv6, host discovery, multiple active hosts, WinRM, SSH, or an agent service.
- Do not silently broaden remote support for local hardware, registry, HCS, USB, or local-file workflows.

## Constraints

- WPF / .NET 8 / WPF-UI 4.3; preserve existing UI conventions.
- WMI/DCOM is the management channel and TCP 2179 is the console channel.
- Keep the worktree's pre-existing untracked design prototype and specification intact.
- UTF-8 without BOM for added source and documentation.
- No plaintext passwords or credential tokens in configuration or logs.

## Risk Assessment

- Remote WMI behavior requires controlled Windows/Hyper-V integration hosts for final proof.
- Existing services sometimes bypass the centralized WMI API or depend on local-only resources.
- The current machine initially has .NET runtimes but no SDK; tooling must be made available before automated tests.
- The active-host migration must use expand/migrate/contract sequencing so the local app stays runnable.

## Child Deliverables

- #2 rolling logs and redaction
- #3 saved host profiles and Windows credentials
- #4 active-host session coordinator
- #5 connection page and two-channel diagnostics
- #6 atomic switching and write guard
- #7 remote VM list and lifecycle
- #8 remote TCP 2179 console
- #9 disconnect, stale data, and reconnect
- #10 capability matrix and feature gating
- #11 Chinese preflight wizard
- #12 confirmed configuration, verification, and rollback
- #13 end-to-end release verification and docs

## Dependency Notes

- The dependency graph matches GitHub issues #2-#13.
- `depends_on` uses `;` for multiple IDs.

## Done-When

- [ ] Every row in `SUBTASKS.csv` is `DONE`.
- [ ] All issue acceptance criteria are proven by current code, tests, and controlled-host verification.
- [ ] Final build, automated tests, UI verification, and documentation checks pass.
