using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
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
        if (string.Equals(variant, "duplicate-controls", StringComparison.OrdinalIgnoreCase))
        {
            // Deliberately reuses the AutomationId "MedicationTextBox" so tests can
            // exercise a genuine, deterministic ambiguous-match scenario against a real
            // UIA tree (two elements in the same window that score identically against
            // a selector built from stable properties alone).
            var duplicate = new TextBox();
            AutomationProperties.SetAutomationId(duplicate, "MedicationTextBox");
            DuplicateControlsPanel.Children.Add(duplicate);
            return;
        }

        if (string.Equals(variant, "duplicate-labels", StringComparison.OrdinalIgnoreCase))
        {
            // A second unnamed field labelled "Observações:" - the label no longer
            // identifies one control, so a label-based selector must be ambiguous.
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = "Observações:", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBox { Width = 300 });
            LabelledFieldsPanel.Children.Add(row);
            return;
        }

        if (!string.Equals(variant, "alternate", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The labelled, AutomationId-less fields swap places: same labels, new positions.
        var first = LabelledFieldsPanel.Children[0];
        LabelledFieldsPanel.Children.RemoveAt(0);
        LabelledFieldsPanel.Children.Add(first);

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

    private void BrowsePrescriptionButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Receitas (*.pdf;*.jpg;*.jpeg;*.png)|*.pdf;*.jpg;*.jpeg;*.png|Todos os arquivos (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            PrescriptionFileTextBox.Text = dialog.FileName;
        }
    }

    private void PrescriptionDropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            try
            {
                PrescriptionImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(files[0]));
            }
            catch (Exception)
            {
                // Not an image: leave the zone as it was.
            }
        }
    }

    /// <summary>
    /// Shows a deterministic 160x100 image: left half solid red (#FF0000), right half solid
    /// blue (#0000FF), so tests can recognise it in a screen capture.
    /// </summary>
    private void ShowSampleImageButton_Click(object sender, RoutedEventArgs e)
    {
        const int width = 160;
        const int height = 100;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var red = x < width / 2;
                pixels[i] = red ? (byte)0 : (byte)255;     // B
                pixels[i + 1] = 0;                          // G
                pixels[i + 2] = red ? (byte)255 : (byte)0; // R
                pixels[i + 3] = 255;                        // A
            }
        }

        PrescriptionImage.Source = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
    }

    private void ShowScannedTextButton_Click(object sender, RoutedEventArgs e) => ShowScannedText("DIPIRONA 500 MG");

    private void ShowOtherScannedTextButton_Click(object sender, RoutedEventArgs e) => ShowScannedText("AMOXICILINA 875 MG");

    /// <summary>Renders <paramref name="text"/> into a bitmap: black on white, like a scan.</summary>
    private void ShowScannedText(string text)
    {
        const int width = 298;
        const int height = 34;
        var visual = new System.Windows.Media.DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, width, height));
            var formatted = new System.Windows.Media.FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface("Arial"),
                22,
                System.Windows.Media.Brushes.Black,
                1.0);
            context.DrawText(formatted, new Point(8, (height - formatted.Height) / 2));
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        ScannedPrescriptionImage.Source = bitmap;
    }

    private sealed record GridRow(string Medication, string Concentration, string Quantity);
}
