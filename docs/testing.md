# Testing

Run the Release build before using `--no-build`:

```powershell
dotnet build Prescriva.Agent.slnx --configuration Release
dotnet test Prescriva.Agent.slnx --configuration Release --no-build
```

The foundation suite has 53 Domain tests and 7 Infrastructure tests. Domain covers selector scoring/ambiguity, configuration validation and boundaries, and immutable session transitions/events. Infrastructure exercises real temporary JSON files and all six action mappings. The Application test project remains an empty scaffold and reports no tests; it is not counted as behavioral coverage.

Focused commands:

```powershell
dotnet test tests/Prescriva.Agent.Domain.Tests --configuration Release --filter FullyQualifiedName~SelectorMatcherTests
dotnet test Prescriva.Agent.slnx --configuration Release --filter FullyQualifiedName~Configuration
dotnet test tests/Prescriva.Agent.Domain.Tests --configuration Release --filter FullyQualifiedName~SessionEngineTests
```

Session tests cover shared semantic actions, forward/back transitions, out-of-stage ignores, required/failed captures, stale optional values, caller-supplied metadata, ordered event snapshots, atomic rollback, clear/cancel/finish, terminal sessions, invalid metadata, unknown actions/triggers, missing/duplicate event IDs, sequence exhaustion and independent sessions. See [session contract](sessions.md) for action semantics.

Use RED → GREEN → REFACTOR for deterministic rules: first observe the test fail for the intended missing behavior, then implement and rerun it, then run the relevant broader suite. Report actual test counts rather than calling a zero-test run a passing behavioral suite.

Later Windows UI Automation tests will require Windows 10/11 x64 and an interactive desktop. Run them separately from pure unit and integration tests. The manual checklist for overlay, cursor, tray and complete TestTarget flow will be documented with prerequisites and expected results when those features arrive.

On a machine with Windows Smart App Control enforced (`HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy!VerifiedAndReputablePolicyState = 1`), the `Prescriva.Agent.Windows.IntegrationTests` Overlay tests can fail to load `Prescriva.Agent.Desktop.dll` (`FileLoadException`, `0x800711C7`, CodeIntegrity event IDs 3033/3077/3118, "did not meet the Enterprise signing level requirements"). Building Release rather than Debug reduces how often this happens, but is not a guaranteed fix: on a machine where Smart App Control has not yet established reputation for the locally built, unsigned assembly, it can still block Release builds identically. `Unblock-File` does not help - this is a reputation check, not a mark-of-the-web/zone-identifier block. If Release still fails with the same error, either sign the build output, wait for Smart App Control to learn the assembly's reputation, or run these tests on a machine/account where Smart App Control is in evaluation or off mode. The commands above default to `--configuration Release` because it is the better starting point, not because it eliminates the issue.
