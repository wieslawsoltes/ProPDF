using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using ProPDF.Presentation;

namespace ProPDF.Wpf;

public sealed class PdfNavigationPanel : UserControl
{
    public PdfNavigationPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "BOOKMARKS", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) });
        var bookmarks = new ListBox { Height = 210, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        bookmarks.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(PdfWorkspace.NavigationBookmarks)));
        bookmarks.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(PdfWorkspace.SelectedBookmark)) { Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(bookmarks, "Hierarchical document bookmarks");
        panel.Children.Add(bookmarks);
        var actions = new WrapPanel(); panel.Children.Add(actions);
        AddButton(actions, "Go", nameof(PdfWorkspace.FollowBookmarkCommand));
        AddButton(actions, "Back", nameof(PdfWorkspace.BackCommand));
        AddButton(actions, "Forward", nameof(PdfWorkspace.ForwardCommand));
        var edits = new WrapPanel(); panel.Children.Add(edits);
        AddButton(edits, "Child", nameof(PdfWorkspace.InsertChildBookmarkCommand));
        AddButton(edits, "Rename", nameof(PdfWorkspace.RenameBookmarkCommand));
        AddButton(edits, "Delete", nameof(PdfWorkspace.DeleteBookmarkCommand));
        panel.Children.Add(new TextBlock { Text = "Child and Rename use the toolbar text. New bookmarks target the current page.", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 7) });
        panel.Children.Add(new TextBlock { Text = "LINKS", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 7) });
        var links = new ListBox { Height = 150 };
        links.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(PdfWorkspace.NavigationLinks)));
        links.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(PdfWorkspace.SelectedLink)) { Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(links, "Document links");
        panel.Children.Add(links);
        var linkActions = new WrapPanel(); panel.Children.Add(linkActions);
        AddButton(linkActions, "Follow", nameof(PdfWorkspace.FollowLinkCommand));
        AddButton(linkActions, "Copy URI", nameof(PdfWorkspace.CopyLinkCommand));
        AddButton(linkActions, "Link region", nameof(PdfWorkspace.LinkSelectionCommand));
        panel.Children.Add(new TextBlock { Text = "Select a region and enter a destination page in the toolbar text to create a link. External links require confirmation.", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 7) });
        var refresh = new WrapPanel(); panel.Children.Add(refresh);
        AddButton(refresh, "Refresh navigation", nameof(PdfWorkspace.ReloadNavigationCommand));
    }
    private static void AddButton(Panel parent, string text, string command)
    {
        var button = new Button { Content = text, Name = command.Replace("Command", "", StringComparison.Ordinal) + "Button" };
        button.SetBinding(ButtonBase.CommandProperty, new Binding(command));
        AutomationProperties.SetName(button, text);
        parent.Children.Add(button);
    }
    internal static void Install(PdfEditor owner)
    {
        var stack = new Stack<DependencyObject>(); stack.Push(owner);
        while (stack.TryPop(out var current))
        {
            if (current is TabControl tabs)
            {
                if (!tabs.Items.OfType<TabItem>().Any(tab => tab.Content is PdfNavigationPanel))
                    tabs.Items.Add(new TabItem { Header = "Navigate", Content = new PdfNavigationPanel() });
                return;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>()) stack.Push(child);
        }
    }
}
