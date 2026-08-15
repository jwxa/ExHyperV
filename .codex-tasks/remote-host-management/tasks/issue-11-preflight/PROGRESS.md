# Progress Log

## Context Recovery Block

- **Current milestone**: #4 - Run complete issue validation
- **Current status**: DONE
- **Last completed**: Issue #11 Chinese read-only configuration preflight wizard
- **Current artifact**: `.codex-tasks/remote-host-management/tasks/issue-11-preflight/TODO.csv`
- **Key context**: This issue detects and previews only. Applying changes, exact `确认`, rollback, and verification stay in Issue #12.
- **Known issues**: Controlled host `10.0.0.6` is currently unreachable; deterministic adapters provide normal development acceptance.
- **Next action**: Start Issue #12 confirmed configuration, verification, and rollback.

## Design Notes

- The preflight dependency graph deliberately contains no write interface.
- Keep WMI mechanics in one Windows adapter while splitting detection sequencing and plan policy into independent services.
- Represent built-in groups by stable SID-backed kinds, not localized display names.
- Preserve partial facts when one namespace or query fails.

## Session 2026-08-14

- Added read-only preflight contracts, immutable facts, ordered Chinese evidence, a pure planner, CIDR normalization, and the Windows WMI/DCOM reader. No write-capable interface exists in the Issue #11 dependency graph.
- Detects workgroup/domain state, enabled local accounts, stable SID-backed built-in groups, token-filter policy, all network profiles and IPv4 CIDRs, WMI/Hyper-V firewall groups, and the ExHyperV TCP 2179 rule scope.
- Generates only conditional least-privilege recommendations: `Hyper-V Administrators`, `Remote Management Users`, optional workgroup token-filter policy, explicit Public-to-Private changes, built-in firewall groups, and CIDR-scoped TCP 2179.
- Added the four-step Chinese wizard to the host connection page with dynamic active-step styling, responsive sidebar/top-step-strip layout, detailed logs, disabled read-only completion state, explicit Public-to-Private choice, and multiple CIDR selection.
- Added cancellation and disposal for host changes, tab changes, and page disposal. Late results from readers that ignore cancellation cannot replace a cleared target.
- Added ViewModel tests for group membership display, active step state, malformed domain accounts, explicit Public-to-Private choice, CIDR preview content, and late cancelled results. Final automated result: 85/85 tests.
- Release WPF build passes with 0 errors and 248 existing warnings. The known `SSH.NET 2024.1.0` NU1903 warning remains outside this issue.
- All 31 source XAML files parse. The 23 Issue #11 source, test, renderer, and task text files checked are valid UTF-8 without BOM. `git diff --check` passes with existing LF/CRLF notices only.
- Offline real-WPF evidence covers all four steps at 1100x800 and 720x900. Wide layout uses the sidebar; narrow layout uses the top step strip; visual inspection found no overlap after fixing account selection contrast.
- Evidence: `raw/issue-11-preflight-step-{1..4}-{wide|narrow}.png` and matching `.txt` files.
- Controlled host `10.0.0.6` remains unreachable, so deterministic readers provide normal development acceptance without weakening the read-only contract.
