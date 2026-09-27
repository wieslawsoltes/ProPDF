using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ProPDF.Avalonia;

/// <summary>Reusable inspector for revision-aware native page-content editing.</summary>
public sealed partial class PdfContentPanel : UserControl
{
    public PdfContentPanel() => AvaloniaXamlLoader.Load(this);
}
