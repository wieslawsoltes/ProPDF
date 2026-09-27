using System.Collections.Immutable;
using System.Text;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Pdf;
using ProPDF.Core;

namespace ProPDF.Editing.iText;

public sealed partial class ITextPdfEditor : IPdfFormDataService
{
    public Task<PdfFormData> ReadFormDataAsync(PdfSnapshot source, PdfFormDataReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var settings = options ?? new PdfFormDataReadOptions();
        var limits = settings.Limits ?? new PdfFormDataLimits(); limits.Validate();
        return Task.Run(() =>
        {
            using var input = source.OpenRead();
            using var document = new PdfDocument(CreateReader(input, source.GetPassword()));
            if (!document.GetReader().IsOpenedWithFullPermission()) throw new UnauthorizedAccessException("Form export requires owner-authorized access.");
            var form = PdfAcroForm.GetAcroForm(document, false);
            if (form is null) return new PdfFormData(Array.Empty<PdfFormValue>(), limits);
            if (form.HasXfaForm()) throw new NotSupportedException("XFA data exchange is not supported by this adapter.");
            var values = new List<PdfFormValue>();
            foreach (var pair in form.GetAllFormFields())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var field = pair.Value;
                if (!IsValueField(field) || field.IsNoExport() || !settings.IncludeReadOnlyFields && field.IsReadOnly()) continue;
                if (!settings.IncludePasswordFields && PdfName.Tx.Equals(field.GetFormType()) && (field.GetFieldFlags() & 8192) != 0) continue;
                if (values.Count == limits.MaximumFields) throw new InvalidDataException("Form export exceeds the field limit.");
                values.Add(new PdfFormValue(pair.Key, FieldValues(field.GetValue())));
            }
            return new PdfFormData(values, limits);
        }, cancellationToken);
    }

    /// <summary>Checks required values and text length; never runs JavaScript or cross-field calculation rules.</summary>
    public Task<IReadOnlyList<PdfFormValidationIssue>> ValidateFormDataAsync(PdfSnapshot source, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<PdfFormValidationIssue>>(() =>
        {
            using var input = source.OpenRead();
            using var document = new PdfDocument(CreateReader(input, source.GetPassword()));
            var form = PdfAcroForm.GetAcroForm(document, false);
            if (form is null) return Array.Empty<PdfFormValidationIssue>();
            if (form.HasXfaForm()) throw new NotSupportedException("XFA validation is not supported.");
            var issues = new List<PdfFormValidationIssue>();
            var count = 0;
            foreach (var pair in form.GetAllFormFields())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > 100_000) throw new InvalidDataException("Form validation exceeds its field limit.");
                var field = pair.Value;
                if (!IsValueField(field)) continue;
                var values = FieldValues(field.GetValue());
                var missing = values.Length == 0 || values.All(string.IsNullOrWhiteSpace) || PdfName.Btn.Equals(field.GetFormType()) && values.All(value => value == "Off");
                if (field.IsRequired() && missing) issues.Add(new PdfFormValidationIssue(pair.Key, "required", "A required field has no value."));
                var maximum = field.GetPdfObject().GetAsNumber(PdfName.MaxLen)?.IntValue();
                if (PdfName.Tx.Equals(field.GetFormType()) && maximum is > 0 && values.Any(value => value.EnumerateRunes().Count() > maximum.Value))
                    issues.Add(new PdfFormValidationIssue(pair.Key, "length", "The field exceeds its configured maximum length."));
            }
            return issues.AsReadOnly();
        }, cancellationToken);

    private static bool IsValueField(PdfFormField field)
    {
        var kind = field.GetFormType();
        return PdfName.Tx.Equals(kind) || PdfName.Ch.Equals(kind) || PdfName.Btn.Equals(kind) && (field.GetFieldFlags() & 65536) == 0;
    }

    private static ImmutableArray<string> FieldValues(PdfObject? value)
    {
        if (value is null || value is PdfNull) return [];
        if (value is PdfIndirectReference reference) value = reference.GetRefersTo();
        if (value is PdfString text) return [text.ToUnicodeString()];
        if (value is PdfName name) return [name.GetValue()];
        if (value is PdfArray array)
        {
            if (array.Size() > 4096) throw new InvalidDataException("Too many native form values.");
            return Enumerable.Range(0, array.Size()).Select(index => array.Get(index) switch
            {
                PdfString item => item.ToUnicodeString(),
                PdfName item => item.GetValue(),
                _ => throw new InvalidDataException("Native form values must be strings or names.")
            }).ToImmutableArray();
        }
        throw new NotSupportedException("The field value cannot be represented as plain form data.");
    }
}
