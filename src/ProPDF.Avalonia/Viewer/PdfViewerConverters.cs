using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace ProPDF.Avalonia;

public sealed class PdfViewerEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
