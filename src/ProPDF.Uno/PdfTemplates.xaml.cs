namespace ProPDF.Uno;
public sealed partial class PdfTemplates : ResourceDictionary { public PdfTemplates() => InitializeComponent(); }
public sealed record PdfUnoPage(PdfViewportController Controller, int Number) { public string Label => $"Page {Number}"; }
