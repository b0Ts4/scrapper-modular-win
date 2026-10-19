using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Prescriva.Agent.Application.Configuration;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Application.Runtime;
using Prescriva.Agent.Application.Templates;
using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Desktop.Configuration;
using Prescriva.Agent.Desktop.Templates;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Infrastructure.Templates;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Desktop;

/// <summary>
/// Templates (plan 15): step 1 lists the templates (shipped next to the executable in
/// <c>Templates</c>, and the person's own under the data directory); step 2 shows the chosen
/// template's checklist, where each item is marked, tested, skipped or adjusted on its own.
/// All logic lives in <see cref="TemplateGuideViewModel"/>; this part only wires the controls.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan ClickWait = TimeSpan.FromSeconds(20);

    // A question window (plan 17) comes after the person goes through the program's own steps
    // (FórmulaCerta: about eight confirmations before "Mais Fórmula(s)?").
    private static readonly TimeSpan WindowWait = TimeSpan.FromMinutes(3);

    private static TimeSpan WaitFor(TriggerDefinition trigger) =>
        trigger.ObservedEvent == TriggerEvents.WindowShown ? WindowWait : ClickWait;

    private JsonTemplateStore? _templateStore;
    private TemplateGuideViewModel? _guide;
    private IReadOnlyList<StoredTemplate> _templates = [];
    private string? _guideMarkingItemId;

    private JsonTemplateStore TemplateStore => _templateStore ??= new JsonTemplateStore(
        Path.Combine(AppContext.BaseDirectory, "Templates"),
        Path.Combine(DataDirectory, "templates"));

    private TemplateGuideViewModel Guide => _guide ??= new TemplateGuideViewModel(_editorViewModel, WaitForOneClickAsync);

    private async Task RefreshTemplatesAsync()
    {
        try
        {
            _templates = await TemplateStore.ListAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _templates = [];
            SetStatus($"Não foi possível ler os modelos ({ex.GetType().Name}).");
        }

        TemplatesList.ItemsSource = _templates
            .Select(entry => $"{entry.Template.Name}{(entry.IsBuiltIn ? string.Empty : "  (seu modelo)")}")
            .ToList();
        TemplatesEmptyText.Visibility = _templates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private StoredTemplate? SelectedTemplate =>
        TemplatesList.SelectedIndex is var index && index >= 0 && index < _templates.Count ? _templates[index] : null;

    private void TemplatesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedTemplate;
        UseTemplateButton.IsEnabled = selected is not null;
        AutoSetupButton.IsEnabled = selected is not null || _autoSetup is not null;
        TemplateDescriptionText.Text = selected?.Template.Description ?? string.Empty;
        TemplateHowToOpenText.Text = selected is null ? string.Empty : "Antes: " + selected.Template.Program.HowToOpen;
    }

    private async void UseTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { Template: var template })
        {
            SetStatus("Escolha um modelo na lista.");
            return;
        }

        // The program chosen in the list when it fits the template; otherwise the open window that does.
        var chosen = OpenWindowsList.SelectedItem is OpenWindowItem { Info: var info } && template.Program.Matches(info.ProcessName, info.WindowTitle)
            ? info
            : null;
        if (chosen is null)
        {
            await RefreshOpenWindowsAsync();
            chosen = _openWindows.Select(item => item.Info).FirstOrDefault(window => template.Program.Matches(window.ProcessName, window.WindowTitle));
        }

        if (chosen is null)
        {
            SetStatus($"A tela deste modelo não está aberta. {template.Program.HowToOpen} Depois clique em 'Usar este modelo' de novo.");
            return;
        }

        try
        {
            // The program's window is named by the part of its title the template gives (plan 18),
            // so a program update that changes the version in the title keeps working.
            var application = new ApplicationDefinition(chosen.ProcessName + ".exe", template.Program.WindowTitleContains);
            var integrationId = SemanticIdGenerator.FromName(template.Id, _configurationStore.ListIds(), "integracao");
            Guide.Start(template, application, integrationId, LocateOpenWindow);
            ProcessIdentityBox.Text = application.ProcessIdentity;
            WindowRuleBox.Text = application.WindowRule;
            IntegrationIdBox.Text = integrationId;
            ChosenProgramText.Text = $"Programa escolhido: {chosen.AppName} — janela '{chosen.WindowTitle}'.";
            GuideSaveTemplateNameBox.Text = template.Name + " (meu)";
            WizardSteps.SelectedItem = Step2Tab;
            RefreshGuide(selectIndex: 0);
            SetStatus($"Modelo '{template.Name}' aberto para a janela '{chosen.WindowTitle}'. Siga o roteiro no passo 2, item por item.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            SetStatus($"Não foi possível usar o modelo: {ex.Message}");
        }
    }

    private CancellationTokenSource? _autoSetup;
    private static readonly TimeSpan AutoSetupRetry = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Plug-and-play setup (plan 18): one click creates the integration with every field and
    /// trigger the template suggests, saves it, verifies it - waiting, with plain instructions,
    /// until the program's screens are open - then approves, activates it and turns on
    /// "Iniciar com o Windows". Clicking again stops it.
    /// </summary>
    private async void AutoSetupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoSetup is not null)
        {
            await _autoSetup.CancelAsync();
            return;
        }

        if (SelectedTemplate is not { Template: var template })
        {
            SetStatus("Escolha um modelo na lista.");
            return;
        }

        using var setup = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        _autoSetup = setup;
        AutoSetupButton.Content = "Parar a configuração automática";
        UseTemplateButton.IsEnabled = false;
        try
        {
            if (_monitorViewModel.IsMonitoring)
            {
                await _monitorViewModel.StopAsync();
            }

            var application = new ApplicationDefinition(template.Program.ProcessIdentity, template.Program.WindowTitleContains);
            Guide.Start(template, application, template.Id, LocateOpenWindow);
            ProcessIdentityBox.Text = application.ProcessIdentity;
            WindowRuleBox.Text = application.WindowRule;
            IntegrationIdBox.Text = template.Id;
            var left = Guide.UseAllSuggestions();
            await _editorViewModel.SaveAsync();
            RefreshSavedIntegrations();
            RefreshGuide(selectIndex: 0);

            var report = await VerifyUntilReadyAsync(template, setup.Token);
            var configuration = _editorViewModel.Configuration!;
            var approval = await _approvalService.RecordAsync(report);
            var status = await _monitorViewModel.StartAsync(configuration, approval);
            if (status != ActivationStatus.Activated)
            {
                ShowAutoSetup($"Configurado e conferido, mas não foi possível ativar: {_monitorViewModel.StatusText}");
                return;
            }

            await _monitoringPreference.SetActiveConfigurationIdAsync(configuration.Id, CancellationToken.None);
            await SetStartWithWindowsAsync(true);
            await RefreshApprovalStateAsync();
            WizardSteps.SelectedItem = Step5Tab;
            ShowAutoSetup($"Pronto: '{template.Name}' configurado com {configuration.Fields.Length} campo(s) e {configuration.Triggers.Length} gatilho(s), conferido, aprovado e ativo. "
                + "O Agent fica na bandeja e volta sozinho ao ligar o computador."
                + (left.Count > 0 ? $" Sem posição sugerida (não incluídos): {string.Join(", ", left.Select(item => item.Name))}." : string.Empty));
        }
        catch (OperationCanceledException)
        {
            ShowAutoSetup("Configuração automática interrompida. A integração ficou salva; clique de novo para continuar.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ConfigurationValidationException or IOException)
        {
            ShowAutoSetup($"Não foi possível configurar automaticamente: {ex.Message}");
        }
        finally
        {
            _autoSetup = null;
            AutoSetupButton.Content = "Configurar automaticamente";
            AutoSetupButton.IsEnabled = UseTemplateButton.IsEnabled = SelectedTemplate is not null;
        }
    }

    /// <summary>Verifies the integration again every few seconds until every field is read and every trigger is watched.</summary>
    private async Task<IntegrationTestReport> VerifyUntilReadyAsync(IntegrationTemplate template, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configuration = _editorViewModel.Configuration!;
            var running = await FindRunningProcessIdsAsync(configuration.Application);
            if (running.Count != 1)
            {
                ShowAutoSetup(running.Count == 0
                    ? $"Aguardando a tela do programa: {template.Program.HowToOpen}"
                    : $"Há {running.Count} janelas '{configuration.Application.WindowRule}' abertas: deixe só uma.");
                await Task.Delay(AutoSetupRetry, cancellationToken);
                continue;
            }

            var instance = new ApplicationInstance(Guid.NewGuid(), running[0]);
            var mainProcess = configuration.Application.ProcessIdentity;
            var runner = new IntegrationTestRunner(
                ProcessRouting.Resolver(mainProcess, _runtimeFactories.CreateSelectorResolver, instance),
                _runtimeFactories.CreateCaptureProvider(instance),
                ProcessRouting.Triggers(mainProcess, _runtimeFactories.CreateTriggerProvider, instance, Guid.NewGuid()),
                triggerTimeout: TimeSpan.FromSeconds(10));
            ShowAutoSetup("Conferindo campos e gatilhos...");
            var report = await runner.VerifyAsync(configuration, cancellationToken);
            if (report.AllPassed)
            {
                return report;
            }

            ShowAutoSetup("Ainda falta: " + DescribeMissing(report, configuration, template) + " Tentando de novo em alguns segundos...");
            await Task.Delay(AutoSetupRetry, cancellationToken);
        }
    }

    /// <summary>What still fails, in words: the screens to open (or uncover) for the items that were not found or read.</summary>
    private static string DescribeMissing(IntegrationTestReport report, IntegrationConfiguration configuration, IntegrationTemplate template)
    {
        var failingFields = report.FieldResults.Where(result => result.Outcome != FieldCheckOutcome.Found).Select(result => result.FieldId);
        var failingTriggers = report.TriggerResults.Where(result => result.Outcome is not (TriggerCheckOutcome.Ready or TriggerCheckOutcome.Detected)).Select(result => result.TriggerId);
        var screens = failingFields.Concat(failingTriggers)
            .Select(id => template.Items.FirstOrDefault(item => item.Id == id))
            .Where(item => item is not null)
            .Select(item => item!.Program ?? template.Program)
            .Distinct()
            .Select(program => $"a tela '{program.WindowTitleContains}' aberta e visível ({program.HowToOpen})");
        var unreadable = report.FieldResults.Where(result => result.Outcome == FieldCheckOutcome.Unreadable).Select(result => result.FieldId).ToArray();
        return string.Join("; ", screens) + "."
            + (unreadable.Length > 0 ? $" Não deu para ler: {string.Join(", ", unreadable)} - não deixe outra janela por cima." : string.Empty);
    }

    private void ShowAutoSetup(string text)
    {
        AutoSetupStatusText.Text = text;
        SetStatus(text);
    }

    /// <summary>The open window of another program a checklist item lives in (plan 16), from the last refreshed list.</summary>
    private ApplicationDefinition? LocateOpenWindow(TemplateProgram program) =>
        _openWindows.Select(item => item.Info).FirstOrDefault(window => program.Matches(window.ProcessName, window.WindowTitle)) is { } found
            ? new ApplicationDefinition(found.ProcessName + ".exe", found.WindowTitle)
            : null;

    /// <summary>Another integration is created or opened: the checklist no longer applies.</summary>
    private void LeaveGuide()
    {
        _guideMarkingItemId = null;
        _guide?.Reset();
        RefreshGuide();
    }

    private GuideItem? SelectedGuideItem => GuideItemsList.SelectedItem as GuideItem;

    private void RefreshGuide(int? selectIndex = null)
    {
        var active = _guide?.IsActive == true;
        GuideCard.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active)
        {
            GuideItemsList.ItemsSource = null;
            return;
        }

        var keep = selectIndex ?? GuideItemsList.SelectedIndex;
        GuideItemsList.ItemsSource = null;
        GuideItemsList.ItemsSource = _guide!.Items.ToList();
        GuideItemsList.SelectedIndex = Math.Clamp(keep, -1, _guide.Items.Count - 1);
        GuideTitleText.Text = "Roteiro do modelo: " + _guide.Template!.Name;
        GuideProgressText.Text = $"{_guide.DoneCount} de {_guide.Items.Count} itens testados ou pulados.";
        ShowGuideItem();
    }

    private void GuideItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowGuideItem();

    private void ShowGuideItem()
    {
        var item = SelectedGuideItem;
        GuideUseSuggestionButton.IsEnabled = item?.HasSuggestion == true;
        GuideMarkButton.IsEnabled = GuideTestButton.IsEnabled = GuideSkipButton.IsEnabled = item is not null;
        if (item is null)
        {
            GuideItemNameText.Text = "Escolha um item na lista.";
            GuideItemHelpText.Text = GuideItemResultText.Text = string.Empty;
            return;
        }

        GuideItemNameText.Text = $"{item.Name} ({item.KindText}) — {item.StatusText}";
        GuideItemHelpText.Text = item.Help
            + (item.Program is { } program ? $" Fica na janela '{program.WindowTitleContains}'." : string.Empty)
            + (item.FileSource is { } source ? $" Arquivo: '{source.Pattern}' em {source.Folder}." : string.Empty)
            + (item.HasSuggestion ? " Há uma posição sugerida para este item." : " Sem posição sugerida: marque-o no programa.");
        GuideItemResultText.Text = item.LastResult;
        GuideItemResultText.Foreground = item.Status switch
        {
            GuideItemStatus.Passed => System.Windows.Media.Brushes.DarkGreen,
            GuideItemStatus.Failed => System.Windows.Media.Brushes.Firebrick,
            _ => System.Windows.Media.Brushes.Black,
        };
        GuideAdjustNameBox.Text = item.Name;
        GuideAdjustRequiredBox.IsChecked = item.Required;
        GuideAdjustKindBox.SelectedIndex = item.FieldKind switch
        {
            FieldKind.File => 1,
            FieldKind.OcrText => 2,
            FieldKind.ScreenImage => 3,
            _ => 0,
        };
        // A file the program opens is found by the template's folder and pattern: its kind stays.
        GuideAdjustKindBox.IsEnabled = item.Kind == Application.Templates.TemplateItemKind.Field && item.FieldKind != FieldKind.RecentFile;
    }

    private async void GuideUseSuggestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGuideItem?.Program is not null)
        {
            await RefreshOpenWindowsAsync(); // the other program's window may have been opened since
        }

        GuideAction(item =>
        {
            Guide.UseSuggestion(item.Id);
            return $"'{item.Name}' marcado com a posição sugerida. Agora clique em 'Testar este item'.";
        });
    }

    private async void GuideMarkButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGuideItem is not { } item)
        {
            return;
        }

        _guideMarkingItemId = item.Id;
        await _inspectorViewModel.StartAsync();
        _pointerPollTimer.Start();
        SetStatus($"Passe o mouse sobre '{item.Name}' no programa (fica contornado) e aperte Ctrl. Ou clique em 'Usar este elemento' mais abaixo.");
    }

    /// <summary>Called when a marking is confirmed: while a checklist item waits to be marked, the element goes to it.</summary>
    private bool TryMarkGuideItem(InspectionState confirmed)
    {
        if (_guideMarkingItemId is not { } id || confirmed.Fingerprint is not { } fingerprint || _guide?.IsActive != true)
        {
            return false;
        }

        _guideMarkingItemId = null;
        _pointerPollTimer.Stop();
        _ = _inspectorViewModel.StopAsync();
        _overlay.UpdateHighlight(null);
        GuideAction(item =>
        {
            Guide.Mark(item.Id, fingerprint);
            return $"'{item.Name}' marcado. Agora clique em 'Testar este item'.";
        }, Guide.Item(id));
        return true;
    }

    private async void GuideTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGuideItem is not { } item)
        {
            return;
        }

        GuideTestButton.IsEnabled = false;
        try
        {
            if (item.Kind == Application.Templates.TemplateItemKind.Button && item.Selector is not null)
            {
                GuideItemResultText.Text = item.Observe == Application.Templates.TemplateTriggerEvent.WindowShown
                    ? $"Agora faça aparecer '{item.Name}' no programa (até {WindowWait.TotalMinutes:0} minutos)..."
                    : $"Agora clique '{item.Name}' no programa ({ClickWait.TotalSeconds:0} segundos)...";
                SetStatus(GuideItemResultText.Text);
            }

            await Guide.TestAsync(item.Id);
            SetStatus($"{item.Name}: {item.LastResult}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            SetStatus($"Não foi possível testar: {ex.Message}");
        }
        finally
        {
            GuideTestButton.IsEnabled = true;
            RefreshGuide();
        }
    }

    private void GuideSkipButton_Click(object sender, RoutedEventArgs e) =>
        GuideAction(item =>
        {
            Guide.Skip(item.Id);
            return $"'{item.Name}' pulado.";
        });

    private void GuideAdjustButton_Click(object sender, RoutedEventArgs e) =>
        GuideAction(item =>
        {
            Guide.Adjust(item.Id, GuideAdjustNameBox.Text, item.FieldKind == FieldKind.RecentFile
                ? FieldKind.RecentFile
                : GuideAdjustKindBox.SelectedIndex switch
                {
                    1 => FieldKind.File,
                    2 => FieldKind.OcrText,
                    3 => FieldKind.ScreenImage,
                    _ => FieldKind.Text,
                }, GuideAdjustRequiredBox.IsChecked == true);
            return $"'{item.Name}' ajustado.";
        });

    private void GuideRemoveItemButton_Click(object sender, RoutedEventArgs e) =>
        GuideAction(item =>
        {
            Guide.RemoveItem(item.Id);
            return $"'{item.Name}' tirado do roteiro.";
        });

    private void GuideAddItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guide?.IsActive != true || string.IsNullOrWhiteSpace(GuideNewItemNameBox.Text))
        {
            SetStatus("Escreva o nome do novo item.");
            return;
        }

        var (kind, fieldKind) = GuideNewItemKindBox.SelectedIndex switch
        {
            1 => (Application.Templates.TemplateItemKind.Field, FieldKind.OcrText),
            2 => (Application.Templates.TemplateItemKind.Field, FieldKind.ScreenImage),
            3 => (Application.Templates.TemplateItemKind.Button, FieldKind.Text),
            _ => (Application.Templates.TemplateItemKind.Field, FieldKind.Text),
        };
        var item = Guide.AddItem(GuideNewItemNameBox.Text, kind, fieldKind);
        GuideNewItemNameBox.Text = string.Empty;
        RefreshGuide(selectIndex: Guide.Items.Count - 1);
        SetStatus($"'{item.Name}' acrescentado ao roteiro. Marque-o no programa.");
    }

    private async void GuideSaveTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guide?.IsActive != true || string.IsNullOrWhiteSpace(GuideSaveTemplateNameBox.Text))
        {
            SetStatus("Dê um nome ao seu modelo.");
            return;
        }

        try
        {
            var name = GuideSaveTemplateNameBox.Text.Trim();
            var id = SemanticIdGenerator.FromName(name, [], "modelo");
            await TemplateStore.SaveAsync(Guide.ToTemplate(id, name), CancellationToken.None);
            await RefreshTemplatesAsync();
            SetStatus($"Modelo '{name}' salvo. Ele aparece no passo 1.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SetStatus($"Não foi possível salvar o modelo: {ex.Message}");
        }
    }

    private void GuideAction(Func<GuideItem, string> action, GuideItem? target = null)
    {
        if ((target ?? SelectedGuideItem) is not { } item)
        {
            SetStatus("Escolha um item no roteiro.");
            return;
        }

        try
        {
            SetStatus($"{action(item)} {UnsavedChanges()}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            SetStatus($"Não foi possível: {ex.Message}");
        }

        RefreshGuide();
    }

    /// <summary>Watches one button of the program for a single click (the checklist's button test).</summary>
    private async Task<bool> WaitForOneClickAsync(TriggerDefinition trigger, CancellationToken cancellationToken)
    {
        var application = _editorViewModel.Configuration?.Application;
        var processIds = application is null ? [] : await FindRunningProcessIdsAsync(application);
        if (processIds.Count == 0)
        {
            return false;
        }

        // A button of another program (plan 16) is watched in that program.
        var provider = ProcessRouting.Triggers(application!.ProcessIdentity, _runtimeFactories.CreateTriggerProvider,
            new ApplicationInstance(Guid.NewGuid(), processIds[0]), Guid.NewGuid());
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(WaitFor(trigger));
        try
        {
            await foreach (var _ in provider.WatchAsync(trigger, wait.Token))
            {
                return true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ElementInspectionFailure or InvalidOperationException)
        {
        }

        return false;
    }
}
