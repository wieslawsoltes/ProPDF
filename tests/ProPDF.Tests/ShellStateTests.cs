using ProPDF.Presentation;
using SkiaSharp;
using Xunit;

namespace ProPDF.Tests;

public sealed class ShellStateTests
{
    [Fact]
    public void DefaultsAreDocumentFirst()
    {
        var shell = new PdfShellState();
        Assert.True(shell.ToolsVisible); Assert.False(shell.PagesVisible); Assert.False(shell.IsSearchOpen);
        Assert.Equal("All tools", shell.Title); Assert.True(shell.IsAllTools);
        Assert.Equal(8, PdfShellState.Categories.Count);
        Assert.Equal(8, PdfShellState.Categories.Select(c => c.Section).Distinct().Count());
    }
    [Theory]
    [InlineData("Edit", 0)] [InlineData("Review", 1)] [InlineData("Forms", 2)] [InlineData("Navigate", 3)]
    [InlineData("Export", 4)] [InlineData("Document", 5)] [InlineData("Organize", 6)] [InlineData("Redact", 7)]
    public void CatalogRoutesToRetainedInspector(string name, int expected)
    {
        var shell = new PdfShellState(); shell.CloseTools(); shell.ShowSectionCommand.Execute(name);
        Assert.True(shell.ToolsVisible); Assert.True(shell.IsInspector); Assert.False(shell.IsAllTools);
        Assert.Equal(expected, shell.InspectorIndex);
        Assert.Equal(PdfShellState.Categories.Single(c => c.Section.ToString() == name).Title, shell.Title);
    }
    [Fact]
    public void ClosingWidePaneIsNotOverriddenByResize()
    {
        var shell = new PdfShellState(); shell.CloseTools(); shell.SetAvailableWidth(1600);
        Assert.False(shell.ToolsVisible);
        shell.TogglePages(); Assert.True(shell.PagesVisible);
        shell.TogglePages(); shell.SetAvailableWidth(1450); Assert.False(shell.PagesVisible);
    }
    [Fact]
    public void CompactDrawersAreExclusiveAndKeepSelection()
    {
        var shell = new PdfShellState(); shell.SetAvailableWidth(390);
        Assert.True(shell.IsCompact); Assert.False(shell.ToolsVisible);
        shell.ShowSection(PdfShellSection.Edit); Assert.True(shell.ToolsVisible); Assert.Equal(300, shell.PanelWidth);
        shell.TogglePages(); Assert.True(shell.PagesVisible); Assert.False(shell.ToolsVisible);
        shell.ShowSection(PdfShellSection.Review); Assert.False(shell.PagesVisible); Assert.True(shell.ToolsVisible);
        shell.SetSearchOpen(true); Assert.True(shell.Dismiss()); Assert.False(shell.IsSearchOpen); Assert.True(shell.ToolsVisible);
        Assert.True(shell.Dismiss()); Assert.False(shell.ToolsVisible);
        Assert.Equal(PdfShellSection.Review, shell.Section);
    }
    [Theory]
    [InlineData(320)] [InlineData(390)] [InlineData(640)] [InlineData(980)] [InlineData(1450)] [InlineData(2560)]
    public void ExplicitPaneChoicesSurviveResize(double width)
    {
        var shell = new PdfShellState(); shell.CloseTools(); shell.SetAvailableWidth(width);
        Assert.False(shell.ToolsVisible); shell.ShowSection(PdfShellSection.Forms); Assert.True(shell.ToolsVisible);
        Assert.InRange(shell.PanelWidth, 180, 300);
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidSizesAreRejected(double width) => Assert.Throws<ArgumentOutOfRangeException>(() => new PdfShellState().SetAvailableWidth(width));
    [Fact]
    public void InvalidCommandsDoNotChangeSelection()
    {
        var shell = new PdfShellState(); Assert.False(shell.ShowSectionCommand.CanExecute("NotATool"));
        shell.ShowSectionCommand.Execute("NotATool"); Assert.True(shell.IsAllTools);
        Assert.Throws<ArgumentOutOfRangeException>(() => shell.ShowSection((PdfShellSection)123));
    }
    [Fact]
    public void UnchangedLayoutDoesNotGenerateNotificationWork()
    {
        var shell = new PdfShellState(); var notifications = 0;
        shell.PropertyChanged += (_, _) => notifications++;
        for (var i = 0; i < 1000; i++) { shell.SetAvailableWidth(1450); shell.ShowSection(PdfShellSection.AllTools); shell.SetSearchOpen(false); }
        Assert.Equal(0, notifications);
    }
    [Fact]
    public void OriginalIconsAreValidVectorPaths()
    {
        foreach (var field in typeof(PdfShellIcons).GetFields())
        {
            using var path = SKPath.ParseSvgPathData((string)field.GetValue(null)!);
            Assert.NotNull(path); Assert.False(path.IsEmpty);
        }
    }
}
