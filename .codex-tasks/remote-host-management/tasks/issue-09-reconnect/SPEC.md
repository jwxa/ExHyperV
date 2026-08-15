# Issue #9 - Disconnect, Stale Data, and Automatic Reconnect

## Task Shape

- **Shape**: `single-full`

## Goals

- Preserve the last successful remote snapshot after a connection loss and mark it as stale.
- Immediately reject new writes and console captures while remote data is stale.
- Automatically reconnect the same active remote host with one cancellable, capped exponential-backoff loop.
- Expose reconnect attempt count, next-attempt time, and the last connection error to the UI.
- Revalidate both channels and reload the basic snapshot before clearing stale state.
- Let the user stop automatic reconnect or explicitly switch to local/another saved host.

## Non-Goals

- Do not silently fall back to the local host.
- Do not add background monitoring unrelated to failures observed by active remote operations or the console.
- Do not implement the full capability matrix; that belongs to Issue #10.
- Do not change the WMI/DCOM or TCP 2179 protocols.

## Deliverables

- Immutable reconnect state in the active-host snapshot.
- Injectable reconnect delay policy for deterministic tests.
- Single-owner reconnect loop with cancellation and bounded exponential backoff.
- Disconnect reporting from active VM operations and console disconnects.
- Connection-page stale/reconnect presentation and stop/retry controls.
- Tests for stale data, write gating, backoff, single-flight behavior, cancellation, recovery, and no local fallback.

## Done-When

- [x] A remote connection loss retains the last snapshot and publishes stale/reconnecting state.
- [x] Writes and console operations are rejected while stale.
- [x] Retry delays grow exponentially up to a fixed cap and only one reconnect runs at a time.
- [x] Users can stop reconnect or trigger an immediate retry.
- [x] Successful reconnect refreshes channels and snapshot before stale state clears.
- [x] Repeated failures keep the same remote host active and expose attempt/error details.
- [x] Tests, full WPF build, controlled-host proof or explicit blocker, UTF-8 checks, XAML parsing, and `git diff --check` pass.

## Final Validation Command

```powershell
dotnet run --project tests/ExHyperV.Tests/ExHyperV.Tests.csproj; dotnet build src/ExHyperV.csproj; git diff --check
```
