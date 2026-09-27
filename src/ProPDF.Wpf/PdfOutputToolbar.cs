using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ProPDF.Presentation;

namespace ProPDF.Wpf;

/// <summary>Reusable native toolbar over the same workspace commands as Avalonia.</summary>
public sealed class PdfOutputToolbar : Border
{
    public PdfOutputToolbar()
    {
        Background = new SolidColorBrush(Color.FromRgb(240, 244, 253));
        BorderBrush = new SolidColorBrush(Color.FromRgb(221, 227, 237));
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(8, 4, 8, 4);
        var panel = new WrapPanel();
        Child = panel;
        void Label(string text) => panel.Children.Add(new TextBlock
        {
            Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0)
        });
        void Input(string property, string label, double width)
        {
            var input = new TextBox { Width = width, Margin = new Thickness(3), MinHeight = 30, FontSize = 12, ToolTip = label };
            input.SetBinding(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            AutomationProperties.SetName(input, label);
            panel.Children.Add(input);
        }
        void Button(string text, string command)
        {
            var button = new Button { Content = text, Name = command.Replace("Command", "", StringComparison.Ordinal) + "Button" };
            button.SetBinding(ButtonBase.CommandProperty, new Binding(command));
            AutomationProperties.SetName(button, text);
            panel.Children.Add(button);
        }
        Label("OUTPUT");
        Input(nameof(PdfWorkspace.ExportDpi), "Image export DPI, 18 to 1200", 55);
        Label("DPI");
        Button("PNG", nameof(PdfWorkspace.ExportPngCommand));
        Button("JPEG", nameof(PdfWorkspace.ExportJpegCommand));
        Input(nameof(PdfWorkspace.PageRange), "Page range: all, odd, even, 1-3, last", 120);
        Button("Export text", nameof(PdfWorkspace.ExportTextCommand));
        Button("Extract range", nameof(PdfWorkspace.ExtractRangeCommand));
        Button("Compare PDF", nameof(PdfWorkspace.CompareCommand));
        Button("Save comparison", nameof(PdfWorkspace.SaveComparisonCommand));
        var results = new ComboBox { Width = 180, Margin = new Thickness(4, 3, 4, 3), MinHeight = 30 };
        results.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(PdfWorkspace.ComparisonPages)));
        results.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(PdfWorkspace.SelectedComparisonPage)) { Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(results, "Compared pages; select to navigate");
        panel.Children.Add(results);
        var summary = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        summary.SetBinding(TextBlock.TextProperty, new Binding(nameof(PdfWorkspace.ComparisonSummary)));
        panel.Children.Add(summary);
    }

    internal static void Install(PdfEditor owner)
    {
        if (owner.Content is not Grid grid || grid.Children.OfType<PdfOutputToolbar>().Any()) return;
        foreach (UIElement child in grid.Children)
        {
            var row = Grid.GetRow(child);
            if (row >= 3) Grid.SetRow(child, row + 1);
        }
        grid.RowDefinitions.Insert(3, new RowDefinition { Height = GridLength.Auto });
        var toolbar = new PdfOutputToolbar();
        Grid.SetRow(toolbar, 3);
        grid.Children.Add(toolbar);
    }
}
