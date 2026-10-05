# Architecture overview

Prescriva Agent is a Windows 10/11 x64 desktop agent that captures **only** the UI elements an operator explicitly configured, when a configured button is pressed, and records business events in a local encrypted queue. See the [milestone design](../superpowers/specs/2026-09-27-prescriva-agent-milestone-1-design.md) and the ADRs in [`docs/decisions`](../decisions).

## Projects and dependency direction

```text
Domain  <-  Application  <-  Infrastructure
   ^             ^   ^             ^
   |             |   +--- Windows  |
   +-------------+--------- Desktop (composition root, WPF)
TestTarget (stand-alone WPF app used as the automation target)
```

| Project | Responsibility | Must not reference |
| --- | --- | --- |
| `Prescriva.Agent.Domain` (`net10.0`) | Configuration model and validation, element fingerprints and selector scoring, the immutable session engine, domain events, `ConfigurationApproval`. | Windows, WPF, UIA, SQLite, DPAPI |
| `Prescriva.Agent.Application` (`net10.0`) | Use-case orchestration over interfaces: inspection controller, selector/capture/trigger contracts, `AgentRuntime` + `SessionCoordinator`, test-mode runner and `ConfigurationFingerprint`, outbox/log/protector contracts. | Concrete adapters |
| `Prescriva.Agent.Infrastructure` (`net10.0`) | JSON configuration store, SQLite event outbox, DPAPI payload protector, retention and capacity policy, JSON-lines technical log. | WPF, UIA |
| `Prescriva.Agent.Windows` (`net10.0-windows10.0.19041.0`) | UI Automation adapters (inspector, selector resolver, capture provider, trigger provider) on one STA `AutomationDispatcher`, process discovery, `UiAutomationRuntimeFactories`. | Infrastructure, Desktop |
| `Prescriva.Agent.Desktop` (`net10.0-windows10.0.19041.0`, WPF) | Composition root and UI: configurator, inspection overlay, test mode, activation/monitoring. View models are plain `INotifyPropertyChanged`. | — |

## End-to-end flow

```text
Operator configures (Inspector + overlay)  ->  JSON configuration (no business values)
          |
          v
Test mode (IntegrationTestRunner) -> report -> ConfigurationApproval bound to the content hash
          |
          v
AgentRuntime.ActivateAsync(configuration, approval)   -- refuses NotTested / edited configs
          |  one SessionCoordinator per running process instance (WindowsApplicationInstanceSource)
          v
UIA Invoke event -> TriggerSignal -> TriggerDeduplicator -> resolve + capture fields
          -> SessionEngine.Apply -> DomainEvent(s) -> SqliteEventOutbox (DPAPI payload)
          -> RuntimeDiagnostic (after persistence) -> Desktop monitor (Portuguese text)
```

Detailed notes: [selector engine](selector-engine.md), [capture engine](capture-engine.md), [event engine](event-engine.md), [security and privacy](security.md).

## Runtime composition (Desktop)

`MainWindow` builds one `AutomationDispatcher`, one `UiAutomationRuntimeFactories` (PID-scoped resolver, capture and trigger provider per application instance), one `SqliteEventOutbox` with `DpapiPayloadProtector`, one `StructuredTechnicalLog` and one `AgentRuntime`, wrapped by `RuntimeMonitorViewModel`. Local data lives in `%LOCALAPPDATA%\Prescriva\Agent` (override with `PRESCRIVA_AGENT_DATA`):

| Path | Contents |
| --- | --- |
| `configurations\<id>.json` | Versioned configuration (selectors, stages, actions). |
| `events.db` | SQLite outbox; payload column is DPAPI ciphertext. |
| `logs\technical.jsonl` | Technical log: IDs, codes, provider, confidence, timings only. |

Retention (`RetentionService`, 7 days for confirmed events) runs when the window loads.
