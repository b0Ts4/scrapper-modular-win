# Testing

Run the Release build before using `--no-build`:

```powershell
dotnet build Prescriva.Agent.slnx --configuration Release
dotnet test Prescriva.Agent.slnx --configuration Release --no-build
```

Task 1 leaves the three xUnit projects empty by design. The command verifies that the test projects load, but it executes zero behavioral tests. Tasks 2–4 add domain and configuration tests. For a focused test, run `dotnet test tests/Prescriva.Agent.Domain.Tests --filter FullyQualifiedName~SelectorMatcherTests` after those tests exist.

Use RED → GREEN → REFACTOR for deterministic rules: first observe the test fail for the intended missing behavior, then implement and rerun it, then run the relevant broader suite. Report actual test counts rather than calling a zero-test run a passing behavioral suite.

Later Windows UI Automation tests will require Windows 10/11 x64 and an interactive desktop. Run them separately from pure unit and integration tests. The manual checklist for overlay, cursor, tray and complete TestTarget flow will be documented with prerequisites and expected results when those features arrive.
