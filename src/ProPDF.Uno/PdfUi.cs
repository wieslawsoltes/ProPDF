namespace ProPDF.Uno;

internal static class PdfUi
{
    internal static void Bind(FrameworkElement element, DependencyProperty property, string path, bool twoWay = false) =>
        element.SetBinding(property, new Binding { Path = new PropertyPath(path), Mode = twoWay ? BindingMode.TwoWay : BindingMode.OneWay,
            UpdateSourceTrigger = twoWay ? UpdateSourceTrigger.PropertyChanged : UpdateSourceTrigger.Default });
    internal static SolidColorBrush Brush(uint rgb) => new(Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    internal static StackPanel Stack(bool horizontal = false) => new() { Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical, Spacing = 6 };
    internal static TextBlock Label(string text, bool heading = false) => new() { Text = text, FontSize = heading ? 12 : 11,
        FontWeight = heading ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = Brush(0x686868), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, heading ? 12 : 2, 0, 4) };
    internal static Button Button(string text, string command)
    {
        var button = new Button { Content = text, Name = command.Replace("Command", "Button", StringComparison.Ordinal),
            Padding = new Thickness(10, 6, 10, 6), FontSize = 12, MinHeight = 32, MinWidth = 0 };
        AutomationProperties.SetName(button, text); AutomationProperties.SetAutomationId(button, button.Name);
        Bind(button, ButtonBase.CommandProperty, command); return button;
    }
    internal static TextBox Input(string path, string label, bool multiline = false)
    {
        var box = new TextBox { Name = path + "Input", PlaceholderText = label, MinWidth = 0, MinHeight = 32, FontSize = 12,
            AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (multiline) box.Height = 90;
        AutomationProperties.SetName(box, label); AutomationProperties.SetAutomationId(box, box.Name);
        Bind(box, TextBox.TextProperty, path, true); return box;
    }
    internal static ComboBox Choice(string source, string selected, string label, string? display = null)
    {
        var box = new ComboBox { Name = selected + "Choice", MinWidth = 0, MinHeight = 32, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (display is not null) box.DisplayMemberPath = display;
        AutomationProperties.SetName(box, label); AutomationProperties.SetAutomationId(box, box.Name);
        Bind(box, ItemsControl.ItemsSourceProperty, source); Bind(box, Selector.SelectedItemProperty, selected, true); return box;
    }
    internal static ListView List(string source, string selected, string label, string? display = null, double height = 180)
    {
        var list = new ListView { Name = source + "List", Height = height, SelectionMode = ListViewSelectionMode.Single,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (display is not null) list.DisplayMemberPath = display;
        AutomationProperties.SetName(list, label); AutomationProperties.SetAutomationId(list, list.Name);
        Bind(list, ItemsControl.ItemsSourceProperty, source); Bind(list, Selector.SelectedItemProperty, selected, true); return list;
    }
    internal static TextBlock BoundText(string path)
    {
        var text = Label(""); Bind(text, TextBlock.TextProperty, path); return text;
    }
    internal static void Actions(Panel parent, params (string text, string command)[] actions)
    {
        var row = Stack(true); foreach (var (text, command) in actions) row.Children.Add(Button(text, command));
        parent.Children.Add(new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled });
    }
    internal static void Field(Panel panel, string label, string path, bool multiline = false)
    { panel.Children.Add(Label(label)); panel.Children.Add(Input(path, label, multiline)); }
}
