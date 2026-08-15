# Progress Log

## Context Recovery Block

- **Current milestone**: #4 - Run automated visual and controlled-host validation
- **Current status**: IN_PROGRESS
- **Last completed**: #3 - Exact-confirmation WPF flow and diagnostic refresh
- **Current artifact**: `.codex-tasks/remote-host-management/tasks/issue-12-configuration/TODO.csv`
- **Key context**: Apply must use WMI/DCOM plus target-local PowerShell, exact `确认`, per-step verification, and reverse idempotent rollback. No WinRM is allowed. Every remote command submission is preceded by an atomic protective idempotent rollback prewrite; known-not-applied entries are removed while unknown/cancelled entries are retained. Local verification passes 178/178 tests and the post-feedback Release build has 0 errors. Post-configuration WMI loss enters the normal stale-data and automatic-reconnect state machine. Explicit credential diagnosis is three-state and does not infer a password error from WMI alone.
- **Known issues**: Controlled host `10.0.0.6` is reachable on WMI/DCOM and TCP 2179, while TCP 445 is closed. The latest read-only production report is `../../raw/controlled-host-configuration-preview-20260815-113353.json`: SMB credential pre-validation is Inconclusive, then WMI/DCOM, TCP 2179, preflight, activation, one-VM snapshot, real one-VM read, and console capture succeed; the preview contains exactly three firewall changes. Live apply and rollback remain required final gates and must not be replaced by deterministic tests.
- **Next action**: Keep live apply and rollback opt-in. Before any mutation, rerun read-only preflight, show the complete plan, and require exact-`确认`; execute the generated rollback script only after separate confirmation.

## 2026-08-15 implementation continuation

- Shared preflight evidence now prints each network display name together with its actual interface index.
- A stale network selection was rejected before configuration; the correct read-only preview passed for interface `26` and CIDR `10.0.0.0/24`.
- The preview plan contains three changes only: built-in WMI firewall rules, built-in Hyper-V firewall rules, and the ExHyperV TCP 2179 rule. It reports no Public-to-Private change because the selected network is already Private.
- Preview report: `../../raw/controlled-host-configuration-preview-20260815-105847.json`; all dangerous switches were false and no rollback script was generated.

## 2026-08-14 UI integration checkpoint

- Added exact-confirmation danger dialog and result/log/rollback presentation to the preflight preview step.
- Isolated configuration runs by selected profile; leaving the preflight tab or changing hosts cancels later work and prevents a stale result from replacing the new target UI.
- Once `Win32_Process.Create` is submitted, the runner waits for the completion marker so an applied mutation cannot be omitted from the rollback script during cancellation.
- Validation: 96/96 combined tests; Release WPF build 0 errors and 248 existing warnings.

## 2026-08-14 visual checkpoint

- Rendered and inspected six real-WPF states: exact confirmation, success result, and partial-failure result at wide and narrow sizes.
- Result views visibly include per-step status, rollback path, and detailed log lines without overlap or clipping.
- Strict Issue #12 encoding check: 21/21 relevant files are valid UTF-8 without BOM. The repository has 69 unrelated pre-existing BOM files, which were not rewritten.

## 2026-08-14 controlled-host checkpoint

- Read-only probe of `10.0.0.6`: ICMP `False`; TCP 135 timeout; TCP 2179 timeout; WMI/DCOM timeout.
- No configuration command was attempted and no confirmation prompt was bypassed.
- Issue #12 remains open and task step 4 remains `IN_PROGRESS` until a real apply and rollback can be recorded.

## 2026-08-15 controlled-host recovery checkpoint

- TCP 135 and TCP 2179 now connect from `10.0.0.14` over `WLAN 3`; the production diagnostic confirms the console channel is available.
- Current-identity WMI/DCOM fails with `0x80070005` in both the general and Hyper-V namespaces, so configuration preflight cannot yet read the target state.
- Every mutation switch remained false. No configuration command, VM write, outage, or rollback action ran.
- The remembered explicit `Jwxa` credential was resolved successfully but WMI/DCOM still returned `AuthenticationFailed`; TCP 2179 remained available. Target-qualified `Jwxa-Laptop\Jwxa` produced the same Access Denied result, so the remaining blocker is target authorization/Remote UAC rather than username ambiguity.
- A fresh post-UI read-only run produced `../../raw/controlled-host-acceptance-20260815-104032.json`: WMI/DCOM, TCP 2179, configuration preflight, atomic activation, remote VM read, and console-session capture all passed. All mutation switches remained false; no target state changed.

## 2026-08-14 controlled-host reachability recheck

- Repeated the read-only probe with independent 20-second limits: ICMP returned no reply; TCP 135, TCP 2179, and current-identity WMI/DCOM timed out again.
- The local endpoint is `10.0.0.14/24` on `WLAN 3`, and Windows selected an on-link route to `10.0.0.6` with no gateway hop.
- `WLAN 3` is `Up` at 2.9 Gbps and the default gateway `10.0.0.1` replies, so the local WLAN path is operational.
- The neighbor cache entry for `10.0.0.6` is `Incomplete` with no link-layer address, proving the current blocker occurs before WMI/DCOM authentication or ExHyperV application logic.
- The only VM registered on this local Hyper-V host is saved and attached to `Default Switch`; no local VM or switch setting was changed because the controlled-host evidence does not establish that it is the intended `10.0.0.6` target.

## 2026-08-14 final safety review

- Corrected `StdRegProv` calls to the standard `root\default` namespace and kept `Win32_*` queries in `root\cimv2`.
- Added a pre-created pending marker before `Win32_Process.Create`; timeouts or uncertain process creation conservatively add the current idempotent rollback command.
- Added compensation for multi-operation firewall changes and a distinct marker when compensation itself fails.
- Made UTF-8 no-BOM rollback scripts compatible with Windows PowerShell 5.1 by decoding executable Chinese text and rollback bodies from UTF-8 Base64 at runtime.
- Fixed per-step verification for multiple selected Public network profiles.
- Final validation: 99/99 tests, Release build 0 errors/248 existing warnings, 32 XAML files parsed, 23/23 relevant files UTF-8 without BOM, and `git diff --check` passed.

## 2026-08-14 implementation audit checkpoint

- Applied the active remote WMI timeout consistently to queries and method invocations, including configuration markers and `Win32_Process.Create`, so a half-open DCOM call cannot bypass the configured operation bound.
- Preserved structured WMI transport failures through VM reads and lifecycle writes; the active-host coordinator now marks snapshots stale, blocks further writes, and starts the existing capped reconnect loop for these failures.
- Added regression coverage proving remote read/write transport failures reconnect, ordinary WMI business failures do not reconnect, and local WMI calls keep their existing timeout behavior.
- Validation: 109/109 tests, including Release preservation of RPC failure causes and bounded remote object reads/Job polling; Release WPF build 0 errors/248 existing warnings; 32 XAML files parsed; 11 touched files UTF-8 without BOM; `git diff --check` passed.

## Design Notes

- Keep safety policy in the preflight planner; the executor accepts only a fresh plan equal to the approved preview.
- Stop on first failure. This produces one deterministic applied prefix and one reverse rollback sequence.
- Use a temporary target registry marker so the WMI command runner can distinguish remote script success from mere process creation.
- Write the rollback script after each successful command and before verification so a verification outage cannot remove the recovery path.

## 2026-08-14 06:05 validation checkpoint

- Deterministic tests pass 134/134, including direct VM mutation capability gates and bounded shutdown when a connector ignores cancellation.
- Release product build succeeds with 0 errors; 32/32 XAML files parse; the 15 latest touched implementation/test files are UTF-8 without BOM; `git diff --check` passes with line-ending notices only.
- The default controlled-host integration entrypoint returns `SKIP` and explicitly performs no network access unless the exact opt-in is supplied.
- Read-only reachability remains blocked: ICMP false, neighbor `Unreachable`, TCP 135 timeout, and TCP 2179 timeout. No WMI, credential, configuration, or rollback action was attempted.

## 2026-08-14 06:50 resumed validation checkpoint

- Revalidated 141/141 deterministic tests and the Release product build with 0 errors; the default integration entrypoint returned `SKIP` without network access.
- Read-only reachability remains blocked: ICMP had 100% loss, the on-link `WLAN 3` neighbor was `Incomplete` with no MAC address, and TCP 135/2179 timed out after 3 seconds.
- No WMI, credential, configuration, rollback, VM mutation, or outage action was attempted.
- Enabled TCP 2179 revalidation on the first confirmed host switch so the activated console capability cannot rely solely on an earlier diagnostic result.
- Revalidated 144/144 deterministic tests, Release product build with 0 errors, default integration `SKIP`, and `git diff --check`; live apply/rollback remains blocked by the same pre-WMI network failure.
- At 2026-08-14 07:13 +08:00, a new read-only probe still returned ICMP false, `WLAN 3` neighbor `Unreachable`, and TCP 135/2179 timeouts. No WMI, credential, configuration, or rollback action ran.
- Hardened the mutation boundary so rollback persistence completes before each remote process submission. A prewrite failure blocks submission; known-not-applied commands shrink the rollback, while cancellation, exceptions, and unknown outcomes retain the protective entry. Deterministic validation is 149/149; real apply/rollback remains pending.
- At 2026-08-14 07:44 +08:00, the latest read-only probe still returned ICMP false, an `Unreachable` all-zero neighbor, and TCP 135/2179 timeouts. No WMI, credential, configuration, or rollback action ran.

## 2026-08-14 firewall semantics audit

- Cross-checked the production System.Management path with NetSecurity: native `Enabled=1` is enabled and `Enabled=2` is disabled; both paths reported 420/163 inbound rules on the local Windows host.
- Converted native protocol IDs to NetSecurity-compatible values (`ICMPv4`, `TCP`, `UDP`, `ICMPv6`) so stale-state assertions and rollback commands agree with target-local PowerShell.
- Refused to modify disabled built-in WMI/Hyper-V rules owned by non-local policy, and detected a fixed-name TCP 2179 rule regardless of direction before accepting it as mutable inbound state.
- Executed generated scripts against in-memory NetSecurity stubs: successful apply plus rollback restored all six captured firewall attributes; an injected address-filter failure restored earlier mutations; a second-rule enable failure disabled the first rule enabled by that attempt.
- Validation: 155/155 tests; Release build 0 errors/248 existing warnings; default integration `SKIP`; 32/32 XAML; UTF-8, CSV, sensitive-assignment review, and `git diff --check` pass. Live controlled-host apply/rollback remains pending.
- A fresh 2026-08-14 08:35 +08:00 read-only probe still showed ICMP false, an `Unreachable` all-zero neighbor, and TCP 135/2179 timeouts after 3016/3000 ms. The process stopped before WMI, credentials, confirmation, or any mutation.

## 2026-08-14 rollback drift audit

- Corrected built-in firewall apply validation so the ownership predicate evaluates each `Where-Object` pipeline rule; rollback continues to evaluate the named `$rule` object.
- Applied the same `Inbound + Local + PersistentStore + resource group` ownership guard before partial-failure compensation disables an enabled built-in rule. Ownership drift now emits `EXHYPERV_ROLLBACK_REQUIRED` and preserves the changed rule.
- Executed four additional runtime PowerShell tests for replaced TCP 2179 rules, built-in resource-group drift, Token policy values `0`/`2`, and `DomainAuthenticated` networks. All refuse third-party overwrite or deletion.
- Validation: 160/160 tests; Release build 0 errors/248 existing warnings; default integration `SKIP`; 6/6 touched source/test/docs files UTF-8 without BOM; 13/13 CSV; `git diff --check` pass.
- A 2026-08-14 10:28 +08:00 read-only probe still showed ICMP false, an `Unreachable` all-zero neighbor, and TCP 135/2179 timeouts after 3013/3000 ms. No WMI, credential, confirmation, configuration, or rollback action ran.
- A 2026-08-14 10:33 +08:00 read-only probe still showed ICMP false, an `Incomplete` all-zero neighbor, and TCP 135/2179 timeouts after 3037/3033 ms. No WMI, credential, confirmation, configuration, or rollback action ran.
- Continued local validation added a regression guard that clears the one-time integration password immediately after capture, even when later option validation fails. Deterministic tests pass 161/161 and the Release product build has 0 errors; live apply/rollback remains blocked before WMI.
- Controlled acceptance now rejects invalid report paths and stops before network access when profile evidence cannot be saved. Local validation passes 162/162 with a 0-error Release build; live apply/rollback remains blocked before WMI.
- Post-configuration diagnostics now pass WMI failure evidence into the active-session coordinator. A real active host transitions to stale data, write freeze, and automatic reconnect instead of remaining in a non-reconnecting unavailable state. Local validation passes 163/163; live apply/rollback remains blocked before WMI.
- The 2026-08-14 11:05 +08:00 read-only recheck still failed before WMI and credentials: ICMP false, `WLAN 3` neighbor `Unreachable` with an all-zero link-layer address, and TCP 135/2179 timeouts after 3012/3000 ms. No configuration or rollback command ran.
- Audit decision: keep the command runner's ownership-checked compensation when the current command applied but its success marker cannot be persisted. This is narrow recovery of an uncommitted step, not automatic execution of the generated full rollback script; full rollback remains exact-`确认` only.
- Revalidated the default controlled-host entrypoint as `SKIP` and the deterministic suite as 167/167. No live configuration or rollback evidence was added.
