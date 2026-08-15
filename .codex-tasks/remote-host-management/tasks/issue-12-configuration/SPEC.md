# Issue #12 - Confirmed configuration, verification, and rollback

## Goal

Apply the exact change preview only after the user types the exact Chinese text `确认`, verify every applied change, generate an accurate rollback script, and rerun the two-channel connection diagnostic.

## Scope

- Re-read the remote host immediately before applying and reject a stale preview.
- Execute configuration through WMI/DCOM `Win32_Process.Create`; do not add WinRM, PowerShell Remoting, SSH, or an agent.
- Apply only the planned least-privilege group, conditional token-policy, explicitly selected network, built-in firewall, and CIDR-scoped TCP 2179 changes.
- Stop after the first failed step and retain an explicit applied/failed list.
- After every confirmed successful mutation, atomically refresh a Chinese rollback script in `logs` before continuing.
- Rerun WMI/DCOM and TCP 2179 diagnostics and refresh the selected or active host state.

## Safety contract

- `确认` comparison is ordinal and exact. Whitespace, lookalikes, and extra text fail.
- Scripts contain no password, credential object, token, or unrestricted remote address.
- Apply scripts assert the preflight state before changing it.
- The Windows command runner waits for the remote process and reads a temporary success marker; process creation alone is never considered success.
- Rollback commands are idempotent, run in reverse order, and restore only values captured immediately before each successful mutation.
- Global dynamic RPC port ranges are never read or modified.

## Out of scope

- Creating users, resetting passwords, granting `Administrators`, IPv6, hostnames, public or `Any` firewall sources.
- Automatic rollback without the user explicitly running and confirming the generated script.
- Changing the WMI/DCOM plus TCP 2179 transport design.

## Acceptance

- Automated tests cover the exact confirmation gate, stale preview, least privilege, conditional changes, CIDR restrictions, partial failure, rollback order/content, and post-apply diagnostics.
- Release WPF build, UTF-8, XAML, diff, and wide/narrow visual checks pass.
- A controlled-host application and rollback is recorded when `10.0.0.6` is reachable; otherwise the issue remains open with the external blocker documented.
