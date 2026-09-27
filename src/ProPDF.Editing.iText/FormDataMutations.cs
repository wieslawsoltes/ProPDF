using System.Text;
using iText.Forms.Fields;
using iText.Kernel.Pdf;
using ProPDF.Core;

namespace ProPDF.Editing.iText;

public sealed partial class ITextPdfEditor
{
    private static void ApplyFormDataEdit(PdfDocument document, PdfFormDataEdit operation, CancellationToken token)
    {
        var form = GetForm(document);
        var fields = form.GetAllFormFields();
        if (fields.Count > 100_000) throw new InvalidDataException("Form field limit exceeded.");
        switch (operation)
        {
            case ImportFormData import:
                ArgumentNullException.ThrowIfNull(import.Data);
                foreach (var value in import.Data.Fields)
                {
                    token.ThrowIfCancellationRequested();
                    if (!fields.TryGetValue(value.Name, out var field))
                    {
                        if (import.IgnoreUnknownFields) continue;
                        throw new KeyNotFoundException(value.Name);
                    }
                    if (field.IsReadOnly() && (import.IgnoreReadOnlyFields || FieldValues(field.GetValue()).SequenceEqual(value.Values))) continue;
                    SetDataValue(field, value.Values, false);
                }
                break;
            case SetFormDefaults defaults:
                ArgumentNullException.ThrowIfNull(defaults.Data);
                foreach (var value in defaults.Data.Fields)
                {
                    token.ThrowIfCancellationRequested();
                    var field = fields.TryGetValue(value.Name, out var found) ? found : throw new KeyNotFoundException(value.Name);
                    ValidateDataValue(field, value.Values, true);
                    PdfObject result;
                    if (PdfName.Btn.Equals(field.GetFormType())) result = new PdfName(value.Values.FirstOrDefault() ?? "Off");
                    else if (field is PdfChoiceFormField && value.Values.Length != 1)
                    {
                        var array = new PdfArray(); foreach (var item in value.Values) array.Add(new PdfString(item)); result = array;
                    }
                    else result = new PdfString(value.Values.FirstOrDefault() ?? "");
                    field.SetDefaultValue(result);
                }
                break;
            case ResetFormData reset:
                var names = reset.AllFields ? fields.Keys : reset.Names.AsEnumerable();
                foreach (var name in names.Distinct(StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    var field = fields.TryGetValue(name, out var found) ? found : throw new KeyNotFoundException(name);
                    if (!IsValueField(field) || field.IsReadOnly() && reset.SkipReadOnly) continue;
                    SetDataValue(field, FieldValues(field.GetDefaultValue()), false);
                }
                break;
            case ConfigureChoiceField configure:
                if (!fields.TryGetValue(configure.Name, out var choiceField) || choiceField is not PdfChoiceFormField choice)
                    throw new ArgumentException("The selected field is not a choice field.");
                if (configure.MultipleSelection && choice.IsCombo()) throw new InvalidOperationException("Combo boxes cannot use multiple selection.");
                if (!configure.MultipleSelection && FieldValues(choice.GetValue()).Length > 1)
                    throw new InvalidOperationException("Select at most one value before disabling multiple selection.");
                choice.SetMultiSelect(configure.MultipleSelection); choice.RegenerateField();
                break;
            case ConfigureTextField configure:
                if (!fields.TryGetValue(configure.Name, out var textField) || !PdfName.Tx.Equals(textField.GetFormType()))
                    throw new ArgumentException("The selected field is not a text field.");
                if (configure.MaximumLength is { } length)
                {
                    if (length is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(configure.MaximumLength));
                    if (length != 0 && FieldValues(textField.GetValue()).Any(value => value.EnumerateRunes().Count() > length))
                        throw new InvalidOperationException("The current value is longer than the requested maximum.");
                    if (length == 0) textField.GetPdfObject().Remove(PdfName.MaxLen);
                    else textField.GetPdfObject().Put(PdfName.MaxLen, new PdfNumber(length));
                }
                if (configure.Multiline is { } multiline) textField.SetFieldFlag(4096, multiline);
                if (configure.Password is { } password) textField.SetFieldFlag(8192, password);
                if (configure.NoExport is { } noExport) textField.SetNoExport(noExport);
                textField.RegenerateField();
                break;
            default: throw new NotSupportedException(operation.GetType().Name);
        }
    }
}
