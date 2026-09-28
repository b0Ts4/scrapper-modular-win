# Setup

## Prerequisites

- Windows 10 or Windows 11, x64, for the Agent runtime and later desktop integration tests.
- .NET 10 SDK. Run `dotnet --version`; the result must start with `10.`. The repository's `global.json` accepts current .NET 10 feature bands.
- Git and a terminal such as PowerShell.

Install the .NET 10 SDK from [Microsoft's download page](https://dotnet.microsoft.com/download/dotnet/10.0) if it is absent. Open a new terminal after installation so `dotnet` is on `PATH`.

## Build

From the repository root:

```powershell
dotnet restore Prescriva.Agent.slnx
dotnet build Prescriva.Agent.slnx --configuration Release
dotnet test Prescriva.Agent.slnx --configuration Release --no-build
```

The first restore downloads xUnit and the .NET test packages. The current solution has no executable Agent or TestTarget yet; those projects are scheduled in later plans. See [current-state](handoffs/current-state.md).
