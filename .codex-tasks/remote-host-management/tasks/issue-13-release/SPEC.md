# Issue #13 - End-to-end verification and release documentation

## Goal

Verify the remote-host workflow across its module boundaries and publish accurate user and maintainer documentation for the WMI/DCOM plus TCP 2179 design.

## Scope

- Exercise saved profiles, current Windows identity, independent channel diagnostics, atomic activation, VM operations, console capability, stale data, and automatic reconnect as one deterministic acceptance flow.
- Verify secrets remain outside profile files, logs, error text, and rollback scripts.
- Document IPv4-only addressing, one active host, partial availability, exact configuration confirmation, rollback, local logs, least privilege, and troubleshooting.
- Supplement the existing English and Chinese READMEs and correct the privacy policy without replacing unrelated hand-written content.
- Record controlled-host results separately from deterministic automation.

## Acceptance

- Automated end-to-end tests cover the cross-module workflow and all existing tests remain green.
- English and Chinese release docs describe the same behavior and limitations.
- Privacy statements match the actual LAN access and local persistence behavior.
- Release build, XAML parsing, UTF-8, diff, and wide/narrow UI checks pass.
- Controlled-host apply/rollback and network-loss exercises are recorded when `10.0.0.6` is reachable; otherwise the issue remains open with the external blocker explicit.
