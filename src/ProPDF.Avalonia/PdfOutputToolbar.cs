using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ProPDF.Presentation;

namespace ProPDF.Avalonia;

/// <summary>Reusable output/compare toolbar. Bind its DataContext to a PdfWorkspace.</summary>
public sealed class PdfOutputToolbar : Border
{
    public PdfOutputToolbar()
    {
        Background = new SolidColorBrush(Color.Parse("#F0F4FD"));
        BorderBrush = new SolidColorBrush(Color.Parse("#DDE3ED"));
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(8, 4);
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        Child = panel;
        void Label(string text) => panel.Children.Add(new TextBlock
        {
            Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0)
        });
        void Input(string property, string label, double width)
        {
            var input = new TextBox { Width = width, Margin = new Thickness(3), MinHeight = 30, FontSize = 12 };
            input.Bind(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay });
            AutomationProperties.SetName(input, label);
            ToolTip.SetTip(input, label);
            panel.Children.Add(input);
        }
        void Button(string text, string command)
        {
            var button = new Button { Content = text, Name = command.Replace("Command", "", StringComparison.Ordinal) + "Button" };
            button.Bind(global::Avalonia.Controls.Button.CommandProperty, new Binding(command));
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
        var results = new ComboBox { Width = 180, Margin = new Thickness(4, 3), MinHeight = 30 };
        results.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(PdfWorkspace.ComparisonPages)));
        results.Bind(global::Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty,
            new Binding(nameof(PdfWorkspace.SelectedComparisonPage)) { Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(results, "Compared pages; select to navigate");
        panel.Children.Add(results);
        var summary = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(PdfWorkspace.ComparisonSummary)));
        panel.Children.Add(summary);
    }

    internal static void Install(PdfEditor owner)
    {
        if (owner.Content is not Grid grid || grid.Children.OfType<PdfOutputToolbar>().Any()) return;
        foreach (var child in grid.Children)
        {
            var row = Grid.GetRow(child);
            if (row >= 3) Grid.SetRow(child, row + 1);
        }
        grid.RowDefinitions.Insert(3, new RowDefinition(GridLength.Auto));
        var toolbar = new PdfOutputToolbar();
        Grid.SetRow(toolbar, 3);
        grid.Children.Add(toolbar);
    }
}
