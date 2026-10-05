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

The first restore downloads xUnit and the .NET test packages. The build produces two executables: `src/Prescriva.Agent.Desktop/bin/Release/net10.0-windows/Prescriva.Agent.Desktop.exe` (the Agent configurator and monitor) and `src/Prescriva.Agent.TestTarget/bin/Release/net10.0-windows/Prescriva.Agent.TestTarget.exe` (the simulated ERP). The Agent keeps configurations, the encrypted event queue and technical logs in `%LOCALAPPDATA%\Prescriva\Agent`; set the `PRESCRIVA_AGENT_DATA` environment variable to use another directory. To start the Agent when you sign in to Windows, check *Iniciar com o Windows* in the Agent (it adds `"…\Prescriva.Agent.Desktop.exe" --background` to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`): it then opens in the notification area and resumes the integration that was left active, if it is still approved. Only one Agent runs per data directory; starting it again shows the running window. See [current-state](handoffs/current-state.md).

Building on Linux/macOS (Windows projects compile with `-p:EnableWindowsTargeting=true`) only checks compilation; the WPF, UI Automation and DPAPI tests require Windows.
