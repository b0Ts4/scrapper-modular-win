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
using Prescriva.Agent.Application.Templates;
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
            var application = new ApplicationDefinition(chosen.ProcessName + ".exe", chosen.WindowTitle);
            var integrationId = SemanticIdGenerator.FromName(template.Id, _configurationStore.ListIds(), "integracao");
            Guide.Start(template, application, integrationId);
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
        GuideItemHelpText.Text = item.Help + (item.HasSuggestion ? " Há uma posição sugerida para este item." : " Sem posição sugerida: marque-o no programa.");
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
        GuideAdjustKindBox.IsEnabled = item.Kind == Application.Templates.TemplateItemKind.Field;
    }

    private void GuideUseSuggestionButton_Click(object sender, RoutedEventArgs e) =>
        GuideAction(item =>
        {
            Guide.UseSuggestion(item.Id);
            return $"'{item.Name}' marcado com a posição sugerida. Agora clique em 'Testar este item'.";
        });

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
                GuideItemResultText.Text = $"Agora clique '{item.Name}' no programa ({ClickWait.TotalSeconds:0} segundos)...";
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
            Guide.Adjust(item.Id, GuideAdjustNameBox.Text, GuideAdjustKindBox.SelectedIndex switch
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

        using var provider = new UiAutomationTriggerProvider(_automationDispatcher, Guid.NewGuid(), processId: processIds[0]);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(ClickWait);
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
