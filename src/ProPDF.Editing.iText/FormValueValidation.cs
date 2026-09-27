using System.Collections.Immutable;
using System.Text;
using iText.Forms.Fields;
using iText.Kernel.Pdf;

namespace ProPDF.Editing.iText;

public sealed partial class ITextPdfEditor
{
    private static void ValidateDataValue(PdfFormField field, ImmutableArray<string> values, bool allowReadOnly)
    {
        if (!IsValueField(field)) throw new NotSupportedException("Signature and pushbutton fields cannot be filled through form-data import.");
        if (!allowReadOnly && field.IsReadOnly()) throw new InvalidOperationException("The form field is read-only.");
        if (values.Length > 1 && (field is not PdfChoiceFormField choice || !choice.IsMultiSelect()))
            throw new ArgumentException("Multiple values require a multi-selection list field.");
        var maximum = field.GetPdfObject().GetAsNumber(PdfName.MaxLen)?.IntValue();
        if (maximum is > 0 && values.Any(value => value.EnumerateRunes().Count() > maximum.Value))
            throw new ArgumentException("The imported field value exceeds its maximum length.");
        if (field is PdfChoiceFormField choiceField && !choiceField.IsEdit())
        {
            var options = choiceField.GetOptions();
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            if (options is not null)
                for (var i = 0; i < options.Size(); i++)
                {
                    if (options.GetAsString(i) is { } item) allowed.Add(item.ToUnicodeString());
                    else if (options.GetAsArray(i)?.GetAsString(0) is { } export) allowed.Add(export.ToUnicodeString());
                }
            if (values.Any(value => value.Length != 0 && !allowed.Contains(value)))
                throw new ArgumentException("An imported choice value is not one of the field's export values.");
        }
        if (PdfName.Btn.Equals(field.GetFormType()))
        {
            var states = field.GetAppearanceStates();
            var value = values.FirstOrDefault() ?? "Off";
            if (value != "Off" && !states.Contains(value, StringComparer.Ordinal))
                throw new ArgumentException("The imported button value is not a valid appearance/export state.");
        }
    }

    private static void SetDataValue(PdfFormField field, ImmutableArray<string> values, bool allowReadOnly)
    {
        ValidateDataValue(field, values, allowReadOnly);
        if (field is PdfChoiceFormField choice)
        {
            var options = choice.GetOptions();
            var selected = new List<int>();
            foreach (var value in values)
            {
                if (value.Length == 0) continue;
                var found = -1;
                if (options is not null)
                    for (var i = 0; i < options.Size(); i++)
                    {
                        var export = options.GetAsString(i)?.ToUnicodeString() ?? options.GetAsArray(i)?.GetAsString(0)?.ToUnicodeString();
                        if (string.Equals(export, value, StringComparison.Ordinal)) { found = i; break; }
                    }
                if (found >= 0) selected.Add(found);
                else if (choice.IsEdit()) { choice.SetValue(value); return; }
            }
            choice.SetListSelected(selected.Distinct().Order().ToArray());
            choice.RegenerateField();
        }
        else SetValue(field, values.FirstOrDefault() ?? (PdfName.Btn.Equals(field.GetFormType()) ? "Off" : ""), allowReadOnly);
    }
}
