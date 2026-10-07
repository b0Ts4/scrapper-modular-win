using System.IO;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using Prescriva.Agent.Infrastructure.Events;
using Prescriva.Agent.Infrastructure.Security;
using Prescriva.Agent.Windows.IntegrationTests.RealApps;
using static Prescriva.Agent.Windows.IntegrationTests.EndToEnd.DesktopDriver;

namespace Prescriva.Agent.Windows.IntegrationTests.EndToEnd;

/// <summary>
/// The whole guided flow against a real Windows program instead of the TestTarget: the
/// Calculator is chosen from the open-program list, its display marked as a field named
/// "Resultado" and its "=" button as "Calcular" (IDs generated from those names), the
/// configuration tested, approved and activated, and pressing "=" after typing 3 9 2 records
/// an event holding the display. Skipped, with the reason, where no calculator exists.
/// </summary>
public sealed class DesktopCalculatorWalkthroughTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "prescriva-calculator-walkthrough-" + Guid.NewGuid().ToString("N"));

    public DesktopCalculatorWalkthroughTests()
    {
        Directory.CreateDirectory(_dataDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [SkippableFact]
    public async Task Operator_integrates_the_real_Calculator_through_the_guided_UI()
    {
        using var calculator = await RealApp.StartAsync("calc.exe", CalculatorIds.ProcessNames);
        Skip.If(calculator is null, "No Windows Calculator on this machine (calc.exe did not open win32calc or CalculatorApp).");
        var ids = CalculatorIds.For(calculator!.Window);
        using var desktop = DesktopProcess.Launch(_dataDirectory);
        var agent = desktop.Window;
        Move(calculator.Element, 0, 0);
        Move(agent, 420, 0);

        // 1. Choose the Calculator from the list of open programs.
        await ChooseProgramAsync(agent, calculator.Window.ProcessName, $"PID {calculator.Window.ProcessId}");
        Assert.Equal(calculator.Window.ProcessName + ".exe", Value(agent, "ProcessIdentityBox"));
        Assert.Equal(calculator.Window.WindowTitle, Value(agent, "WindowRuleBox"));
        SetText(agent, "IntegrationNameBox", "Calculadora");
        Assert.Equal("calculadora", Value(agent, "IntegrationIdBox"));
        Press(agent, "CreateIntegrationButton");
        await WaitForTextAsync(agent, "StatusText", "Integração 'calculadora' criada");

        // 2. The display, named in plain words; its technical ID comes from the name.
        GoToStep(agent, "Step2Tab");
        Press(agent, "StartInspectionButton");
        await HoverAndConfirmElementAsync(agent, calculator.Find(ids.Display), $"AutomationId='{ids.Display}'", "hover-calculator-display");
        SetText(agent, "FieldMeaningBox", "Resultado");
        Assert.Equal("resultado", Value(agent, "FieldSemanticIdBox"));
        Press(agent, "AddFieldButton");
        await WaitForTextAsync(agent, "StatusText", "Campo 'resultado' adicionado");
        Assert.Contains(ListTexts(agent, "FieldsList"), text => text.Contains("Resultado (resultado)", StringComparison.Ordinal));

        // 3. The "=" button reads the display and records an event.
        GoToStep(agent, "Step3Tab");
        await HoverAndConfirmElementAsync(agent, calculator.Find(ids.EqualsButton), $"AutomationId='{ids.EqualsButton}'", "hover-calculator-equals");
        SetText(agent, "TriggerNameBox", "Calcular");
        Assert.Equal("calcular", Value(agent, "TriggerSemanticIdBox"));
        SetCaptureFields(agent, "resultado");
        SetText(agent, "TriggerEmitEventBox", "calculo_feito");
        SelectComboItem(agent, "TriggerTerminalBox", "Nada");
        Press(agent, "AddTriggerButton");
        await WaitForTextAsync(agent, "StatusText", "Gatilho 'calcular' adicionado com 2 ação(ões)");
        Press(agent, "StopInspectionButton");

        // 4. Save and test: type 3 9 2 and press "=" until the run completes; approve.
        Press(agent, "SaveButton");
        await WaitForTextAsync(agent, "StatusText", "Salvo. Alterações não salvas: não");
        foreach (var digit in new[] { 3, 9, 2 })
        {
            calculator.Press(ids.Digits[digit]);
        }

        Press(agent, "PrepareTestButton");
        await WaitForTextAsync(agent, "StatusText", "Teste preparado");
        await WaitForTextAsync(agent, "TestStatusText", "Pronto.");
        Press(agent, "RunTestButton");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!Text(agent, "TestStatusText").StartsWith("Teste concluído", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(400);
            calculator.Press(ids.EqualsButton);
        }

        Assert.StartsWith("Teste concluído", Text(agent, "TestStatusText"), StringComparison.Ordinal);
        Assert.Contains(AllTexts(agent), text => text.StartsWith("Valor lido: ", StringComparison.Ordinal) && text.Contains("392", StringComparison.Ordinal));
        Press(agent, "ApproveButton");
        await WaitForTextAsync(agent, "ApprovalStateText", "Aprovada em");

        // 5. Activate; pressing "=" records the display in the encrypted queue.
        Press(agent, "ActivateButton");
        await WaitUntilAsync(
            () => ListTexts(agent, "DiagnosticsList").Any(text => text.Contains("Monitorando gatilho", StringComparison.Ordinal)),
            () => string.Join(" | ", ListTexts(agent, "DiagnosticsList")) + " / " + Text(agent, "MonitorStatusText"));
        calculator.Press(ids.EqualsButton);
        await WaitForEventsAsync(agent, _dataDirectory, 1);
        Assert.Contains(ListTexts(agent, "EventsList"), text => text.Contains("#1 calculo_feito", StringComparison.Ordinal) && text.Contains("392", StringComparison.Ordinal));

        Press(agent, "StopMonitoringButton");
        await WaitForTextAsync(agent, "MonitorStatusText", "Monitoramento parado");
        desktop.Close();

        var pending = await new SqliteEventOutbox(Path.Combine(_dataDirectory, "events.db"), new DpapiPayloadProtector()).ReadPendingAsync(CancellationToken.None);
        var recorded = Assert.Single(pending);
        Assert.Equal("calculo_feito", recorded.Type);
        Assert.Contains("392", recorded.Payload.Fields["resultado"], StringComparison.Ordinal);
    }
}
