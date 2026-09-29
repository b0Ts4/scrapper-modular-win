using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace Prescriva.Agent.TestTarget;

/// <summary>
/// Deterministic WPF window used only as a target for Windows automation tests.
/// Every interactive control exposes a stable automation ID that must not change
/// across layout variants; only its position on screen may move.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<GridRow> _items = [];
    private int _rowCounter;

    public MainWindow()
    {
        InitializeComponent();
        ItemsGrid.ItemsSource = _items;
        ApplyLayoutVariant(ParseLayoutVariant(Environment.GetCommandLineArgs()));
    }

    internal static string ParseLayoutVariant(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--layout-variant", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return "default";
    }

    private void ApplyLayoutVariant(string variant)
    {
        if (!string.Equals(variant, "alternate", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Same controls, same automation IDs, different screen positions.
        Grid.SetRow(ButtonsPanel, 0);
        Grid.SetRow(QuantityTextBox, 1);
        Grid.SetRow(ConcentrationTextBox, 2);
        Grid.SetRow(MedicationTextBox, 3);
        Grid.SetRow(FormComboBox, 4);
        Grid.SetRow(DynamicFieldPanel, 5);
        Grid.SetRow(ItemsGrid, 6);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        _rowCounter++;
        _items.Add(new GridRow(
            string.IsNullOrWhiteSpace(MedicationTextBox.Text) ? $"Medication {_rowCounter}" : MedicationTextBox.Text,
            string.IsNullOrWhiteSpace(ConcentrationTextBox.Text) ? $"{_rowCounter} mg" : ConcentrationTextBox.Text,
            string.IsNullOrWhiteSpace(QuantityTextBox.Text) ? _rowCounter.ToString() : QuantityTextBox.Text));
    }

    private void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        CompletionStatusText.Visibility = Visibility.Visible;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        MedicationTextBox.Clear();
        ConcentrationTextBox.Clear();
        QuantityTextBox.Clear();
        _items.Clear();
        _rowCounter = 0;
        CompletionStatusText.Visibility = Visibility.Collapsed;
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        // No wizard-step state is tracked; this is a mechanical target for automation.
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        // No wizard-step state is tracked; this is a mechanical target for automation.
    }

    private void ToggleDynamicFieldButton_Click(object sender, RoutedEventArgs e)
    {
        // Toggle enabled state rather than visibility so the control remains
        // discoverable via UI Automation in both states.
        DynamicField.IsEnabled = !DynamicField.IsEnabled;
    }

    private sealed record GridRow(string Medication, string Concentration, string Quantity);
}
