using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Pdf;
using ProPDF.Core;

namespace ProPDF.Editing.iText;

public sealed partial class ITextPdfEditor
{
    private static PdfAcroForm GetForm(PdfDocument document, bool create = false)
    {
        var form = PdfAcroForm.GetAcroForm(document, create);
        if (form is null) throw new InvalidOperationException("This PDF has no AcroForm.");
        if (form.HasXfaForm()) throw new NotSupportedException("XFA forms are not supported by this adapter.");
        return form;
    }

    private static void InsertField(PdfDocument document, AddFormField operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        if (operation.Name.Length > 512 || operation.Name.Contains('.'))
            throw new ArgumentException("New field names must be unique flat names of at most 512 characters.");
        var page = GetPage(document, operation.PageNumber);
        ValidateBounds(page, operation.Bounds);
        var form = GetForm(document, create: true);
        if (form.GetField(operation.Name) is not null) throw new ArgumentException("A field with that name already exists.");
        var rectangle = ToRectangle(Transform(page).ToPdf(operation.Bounds));
        var font = Font("Helvetica", null, operation.Value + string.Concat(operation.Choices));
        PdfFormField field;
        switch (operation.Kind)
        {
            case PdfFormFieldKind.Text:
                field = new TextFormFieldBuilder(document, operation.Name).SetWidgetRectangle(rectangle).SetFont(font).CreateText();
                break;
            case PdfFormFieldKind.CheckBox:
                field = new CheckBoxFormFieldBuilder(document, operation.Name).SetWidgetRectangle(rectangle).CreateCheckBox();
                break;
            case PdfFormFieldKind.ComboBox:
            case PdfFormFieldKind.ListBox:
                if (operation.Choices.IsEmpty) throw new ArgumentException("Choice fields require at least one option.");
                var builder = new ChoiceFormFieldBuilder(document, operation.Name).SetWidgetRectangle(rectangle).SetFont(font).SetOptions(operation.Choices.ToArray());
                field = operation.Kind == PdfFormFieldKind.ComboBox ? builder.CreateComboBox() : builder.CreateList();
                break;
            default: throw new ArgumentOutOfRangeException(nameof(operation.Kind));
        }
        field.SetReadOnly(operation.ReadOnly).SetRequired(operation.Required);
        form.AddField(field, page);
        if (operation.Value.Length != 0) SetValue(field, operation.Value, allowReadOnly: true);
        field.RegenerateField();
    }

    private static void SetFieldValue(PdfDocument document, SetFormValue operation)
    {
        var field = GetForm(document).GetField(operation.Name) ?? throw new KeyNotFoundException(operation.Name);
        SetValue(field, operation.Value, allowReadOnly: false);
    }

    private static void SetValue(PdfFormField field, string value, bool allowReadOnly)
    {
        if (value.Length > 1_000_000) throw new ArgumentOutOfRangeException(nameof(value));
        if (field.IsReadOnly() && !allowReadOnly) throw new InvalidOperationException("The form field is read-only.");
        if (field.GetFormType().Equals(PdfName.Sig)) throw new NotSupportedException("Signature fields require the signing service.");
        if (field.GetFormType().Equals(PdfName.Btn))
        {
            var states = field.GetAppearanceStates();
            var selected = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? states.FirstOrDefault(state => state != "Off") ?? "Yes"
                : value.Equals("false", StringComparison.OrdinalIgnoreCase) ? "Off" : value;
            if (selected != "Off" && states.Length != 0 && !states.Contains(selected))
                throw new ArgumentException("The value does not match an available checkbox/radio export state.", nameof(value));
            field.SetValue(selected);
        }
        else field.SetValue(value);
        field.RegenerateField();
    }
}
