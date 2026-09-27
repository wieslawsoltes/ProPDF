using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using ProPDF.Presentation;

namespace ProPDF.Avalonia;

/// <summary>Reusable navigation and bookmark-authoring inspector over PdfWorkspace.</summary>
public sealed class PdfNavigationPanel : UserControl
{
    public PdfNavigationPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 7 };
        Content = new ScrollViewer { Content = panel };
        panel.Children.Add(new TextBlock { Text = "BOOKMARKS", FontSize = 11, FontWeight = FontWeight.SemiBold });
        var bookmarks = new ListBox { Height = 210 };
        bookmarks.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(PdfWorkspace.NavigationBookmarks)));
        bookmarks.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(PdfWorkspace.SelectedBookmark)) { Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(bookmarks, "Hierarchical document bookmarks");
        panel.Children.Add(bookmarks);
        var bookmarkActions = new WrapPanel();
        panel.Children.Add(bookmarkActions);
        AddButton(bookmarkActions, "Go", nameof(PdfWorkspace.FollowBookmarkCommand));
        AddButton(bookmarkActions, "Back", nameof(PdfWorkspace.BackCommand));
        AddButton(bookmarkActions, "Forward", nameof(PdfWorkspace.ForwardCommand));
        var edits = new WrapPanel(); panel.Children.Add(edits);
        AddButton(edits, "Child", nameof(PdfWorkspace.InsertChildBookmarkCommand));
        AddButton(edits, "Rename", nameof(PdfWorkspace.RenameBookmarkCommand));
        AddButton(edits, "Delete", nameof(PdfWorkspace.DeleteBookmarkCommand));
        panel.Children.Add(new TextBlock { Text = "Child and Rename use the main toolbar text. New bookmarks target the current page.", FontSize = 11, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "LINKS", FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
        var links = new ListBox { Height = 150 };
        links.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(PdfWorkspace.NavigationLinks)));
        links.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(PdfWorkspace.SelectedLink)) { Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(links, "Document links");
        panel.Children.Add(links);
        var linkActions = new WrapPanel(); panel.Children.Add(linkActions);
        AddButton(linkActions, "Follow", nameof(PdfWorkspace.FollowLinkCommand));
        AddButton(linkActions, "Copy URI", nameof(PdfWorkspace.CopyLinkCommand));
        AddButton(linkActions, "Link region", nameof(PdfWorkspace.LinkSelectionCommand));
        panel.Children.Add(new TextBlock { Text = "Select a region and enter a destination page in the toolbar text to create an internal link. External links always require confirmation.", FontSize = 11, TextWrapping = TextWrapping.Wrap });
        var refresh = new WrapPanel(); panel.Children.Add(refresh);
        AddButton(refresh, "Refresh navigation", nameof(PdfWorkspace.ReloadNavigationCommand));
    }
    private static void AddButton(Panel parent, string text, string command)
    {
        var button = new Button { Content = text, Name = command.Replace("Command", "", StringComparison.Ordinal) + "Button" };
        button.Bind(Button.CommandProperty, new Binding(command));
        AutomationProperties.SetName(button, text);
        parent.Children.Add(button);
    }
    internal static void Install(PdfEditor owner)
    {
        var tabs = owner.GetLogicalDescendants().OfType<TabControl>().FirstOrDefault();
        if (tabs is null || tabs.Items.OfType<TabItem>().Any(tab => tab.Content is PdfNavigationPanel)) return;
        tabs.Items.Add(new TabItem { Header = "Navigate", Content = new PdfNavigationPanel() });
    }
}
