# Testing

Run the Release build before using `--no-build`:

```powershell
dotnet build Prescriva.Agent.slnx --configuration Release
dotnet test Prescriva.Agent.slnx --configuration Release --no-build
```

## Current suites (Release, last run 2026-10-04 on GitHub Actions `windows-latest`, Windows Server 2025)

| Project | Tests | Runs on |
| --- | --- | --- |
| `Prescriva.Agent.Domain.Tests` | 58 | any OS |
| `Prescriva.Agent.Infrastructure.Tests` | 31 | Windows (DPAPI tests throw `PlatformNotSupportedException` elsewhere) |
| `Prescriva.Agent.Application.Tests` | 76 | Windows (references the WPF Desktop project) |
| `Prescriva.Agent.Windows.Tests` | 15 | Windows |
| `Prescriva.Agent.Windows.IntegrationTests` | 37 (+30 re-run against a 32-bit TestTarget) | Windows **with an interactive desktop** (launch TestTarget/Desktop, real UI Automation, real cursor) |

Total: 217, all passing, plus the 30-test x86 pass. CI also saves screenshots of every hover (uploaded with the results; small crops are printed in the log). CI (`.github/workflows/ci.yml`) restores, builds Release with warnings as errors, runs the non-interactive suites, then the integration suite, with a 5-minute hang timeout, and uploads `.trx` results.

End-to-end coverage of the milestone:

- `EndToEnd/MilestoneFlowTests` — the production pipeline with no fakes: JSON save/reload, test mode + approval, activation gate (`NotTested` without approval and after an edit), two `item_added` + `budget_finished` from real UIA invokes, a restarted outbox still holding the three events in order, and captured values absent from the database bytes and the technical log.
- `EndToEnd/DesktopWalkthroughTests` — the same flow through the compiled `Prescriva.Agent.Desktop.exe` UI: the real OS cursor hovers TestTarget controls during inspection; the Agent's buttons/text boxes are driven through UIA Invoke/Value patterns; results are read back from the Agent's own status text, diagnostics and events lists, and from the `events.db`/`technical.jsonl` it leaves behind.

- `EndToEnd/DesktopResilienceWalkthroughTests` — Agent restart and reload, approval required again, a required field left empty (visible rejection, no event), the capacity alert, the monitored application closing mid-session (no error), confirmed local-data cleanup, a moved layout and duplicated controls (ambiguous, approval blocked).

Environment knobs used by the tests: `PRESCRIVA_AGENT_DATA` (Agent data directory), `PRESCRIVA_AGENT_OUTBOX_WARNING`/`_CRITICAL` (alert thresholds), `PRESCRIVA_TESTTARGET_EXE` and `PRESCRIVA_TESTTARGET_EXPECTED_BITNESS` (x86 pass), `PRESCRIVA_SCREENSHOT_DIR` (hover screenshots).

The visual checks no automated test can make (overlay drawn in the right place, layout readable) are in the [milestone walkthrough](testing/milestone-1-manual.md).

Focused commands:

```powershell
dotnet test tests/Prescriva.Agent.Domain.Tests --configuration Release --filter FullyQualifiedName~SelectorMatcherTests
dotnet test Prescriva.Agent.slnx --configuration Release --filter FullyQualifiedName~Configuration
dotnet test tests/Prescriva.Agent.Domain.Tests --configuration Release --filter FullyQualifiedName~SessionEngineTests
```

Session tests cover shared semantic actions, forward/back transitions, out-of-stage ignores, required/failed captures, stale optional values, caller-supplied metadata, ordered event snapshots, atomic rollback, clear/cancel/finish, terminal sessions, invalid metadata, unknown actions/triggers, missing/duplicate event IDs, sequence exhaustion and independent sessions. See [session contract](sessions.md) for action semantics.

Use RED → GREEN → REFACTOR for deterministic rules: first observe the test fail for the intended missing behavior, then implement and rerun it, then run the relevant broader suite. Report actual test counts rather than calling a zero-test run a passing behavioral suite.

Windows UI Automation tests require an interactive desktop. Run them separately:

```powershell
dotnet test Prescriva.Agent.slnx --configuration Release --no-build --filter "FullyQualifiedName!~Prescriva.Agent.Windows.IntegrationTests"
dotnet test tests/Prescriva.Agent.Windows.IntegrationTests --configuration Release --no-build
dotnet test tests/Prescriva.Agent.Windows.IntegrationTests --configuration Release --no-build --filter FullyQualifiedName~EndToEnd
```

Do not move the mouse while `DesktopWalkthroughTests` runs: it positions the real cursor.

On Linux/macOS the solution compiles with `-p:EnableWindowsTargeting=true`, and only `Prescriva.Agent.Domain.Tests` (and the non-DPAPI Infrastructure tests) can run.

On a machine with Windows Smart App Control enforced (`HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy!VerifiedAndReputablePolicyState = 1`), any freshly-built or freshly-modified assembly in this solution can fail to load under `testhost.exe` (`FileLoadException`, `0x800711C7`, CodeIntegrity event IDs 3033/3077/3118, "did not meet the Enterprise signing level requirements") — **this is not limited to `Prescriva.Agent.Desktop.dll` or Windows/WPF-hosted assemblies.** It has been reproduced on `Prescriva.Agent.TestTarget.dll` and even on `Prescriva.Agent.Domain.dll`/`Prescriva.Agent.Application.dll` (pure, UI-Automation-free code) immediately after a one-line source edit and rebuild; reverting the edit to restore previously-known-good bytes immediately un-blocks the same assembly. The block is keyed on a local reputation/hash-based Code Integrity check against whatever binary bytes were just produced, not on what the assembly does or references. Building Release rather than Debug reduces how often this happens, but is not a guaranteed fix: on a machine where Smart App Control has not yet established reputation for a locally built, unsigned assembly, it can still block Release builds identically, and it can start blocking a previously-passing assembly again after its very next rebuild. `Unblock-File` does not help - this is a reputation check, not a mark-of-the-web/zone-identifier block. If a test run fails with this error, first try re-running (the block has been observed to clear on its own between runs); if it persists, either sign the build output, wait for Smart App Control to learn the assembly's reputation, or run these tests on a machine/account where Smart App Control is in evaluation or off mode. The commands above default to `--configuration Release` because it is the better starting point, not because it eliminates the issue.
