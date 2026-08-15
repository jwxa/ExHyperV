# Issue #11 - Chinese read-only configuration preflight

## Goal

Add a Chinese remote-host preflight wizard that reads configuration facts, lets the user select an account, network, and IPv4 CIDRs, and produces a complete change preview without modifying the remote host.

## Scope

- Read workgroup/domain state, enabled local accounts, three built-in group memberships, token-filter policy state, network profiles/IPv4 CIDRs, and firewall rule state.
- Keep remote reads behind a read-only session interface with no mutation methods.
- Generate conditional least-privilege recommendations for `Hyper-V Administrators`, `Remote Management Users`, network profile, firewall groups, TCP 2179, and `LocalAccountTokenFilterPolicy`.
- Validate, normalize, and reject invalid, duplicate, or IPv6 CIDRs.
- Show ordered Chinese detection logs and a not-yet-executed change preview in WPF.

## Out of scope

- Applying any account, registry, network, or firewall modification.
- The exact `确认` gate, rollback scripts, post-apply verification, and recovery. These belong to Issue #12.
- Creating users, resetting passwords, granting `Administrators`, changing dynamic RPC ranges, IPv6, hostnames, or network discovery.

## Acceptance

- Opening or running the wizard cannot invoke a write-capable interface because none is present in the preflight dependency graph.
- Partial read failure preserves successful facts and ordered Chinese evidence.
- Public networks require explicit selection in the preview; Private networks never generate that change.
- The token-filter recommendation appears only for workgroup + existing local administrator + missing policy.
- The preview grants only the two accepted groups and restricts TCP 2179 to normalized selected CIDRs.
- Automated tests cover domain/workgroup, Public/Private, multiple NICs, conditional policy, firewall gaps, account selection, and CIDR validation.

## Design

- `IHostPreflightReader` opens a short-lived read session. `IHostPreflightReadSession` exposes only query methods.
- `HostPreflightPipeline` owns sequencing, error isolation, Chinese logs, and application-log integration.
- `HostPreflightPlanner` is a pure function from report + user selection to validation result + change preview.
- `HostPreflightViewModel` owns WPF selection state and never receives credentials or a configuration executor.
