using System.Collections.Immutable;

namespace ProPDF.Core;

public enum PdfFormDataFormat { Xfdf, Json }
public sealed record PdfFormDataLimits(int MaximumBytes = 8 * 1024 * 1024, int MaximumFields = 10_000,
    int MaximumValuesPerField = 256, int MaximumValueCharacters = 1_000_000, int MaximumTotalCharacters = 4 * 1024 * 1024,
    int MaximumNameCharacters = 512, int MaximumDepth = 64)
{
    public void Validate()
    {
        if (MaximumBytes is < 128 or > 128 * 1024 * 1024 || MaximumFields is < 1 or > 100_000 ||
            MaximumValuesPerField is < 1 or > 4096 || MaximumValueCharacters is < 1 or > 4 * 1024 * 1024 ||
            MaximumTotalCharacters is < 1 or > 32 * 1024 * 1024 || MaximumNameCharacters is < 1 or > 4096 || MaximumDepth is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(PdfFormDataLimits));
    }
}

/// <summary>Immutable field values. An empty values array represents clearing a field.</summary>
public sealed record PdfFormValue
{
    public PdfFormValue(string name, IEnumerable<string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(values);
        Name = name;
        Values = values.Take(4097).ToImmutableArray();
        if (Values.Length > 4096 || Values.Any(value => value is null)) throw new ArgumentException("Invalid field value collection.", nameof(values));
    }
    public PdfFormValue(string name, string value) : this(name, new[] { value }) { }
    public string Name { get; }
    public ImmutableArray<string> Values { get; }
}

/// <summary>Bounded, case-sensitive form data independent of any PDF engine or UI framework.</summary>
public sealed class PdfFormData
{
    public PdfFormData(IEnumerable<PdfFormValue> fields, PdfFormDataLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var settings = limits ?? new PdfFormDataLimits(); settings.Validate();
        var copy = fields.Take(settings.MaximumFields + 1).ToArray();
        if (copy.Length > settings.MaximumFields) throw new InvalidDataException("Form data exceeds the field limit.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        long characters = 0;
        foreach (var field in copy)
        {
            if (field is null || field.Name.Length > settings.MaximumNameCharacters || field.Name.Any(char.IsControl) ||
                field.Name.Split('.').Length > settings.MaximumDepth || field.Name.Split('.').Any(part => part.Length == 0))
                throw new InvalidDataException("Invalid or oversized form field name.");
            if (!names.Add(field.Name)) throw new InvalidDataException($"Duplicate field name: {field.Name}");
            if (field.Values.Length > settings.MaximumValuesPerField) throw new InvalidDataException("Too many values for a field.");
            characters += field.Name.Length;
            foreach (var value in field.Values)
            {
                if (value.Length > settings.MaximumValueCharacters) throw new InvalidDataException("Form field value exceeds its character limit.");
                characters += value.Length;
            }
            if (characters > settings.MaximumTotalCharacters) throw new InvalidDataException("Form data exceeds its total character limit.");
        }
        Fields = Array.AsReadOnly(copy);
    }
    public IReadOnlyList<PdfFormValue> Fields { get; }
    public override string ToString() => $"Form data: {Fields.Count} fields; values omitted";
}

public sealed record PdfFormDataReadOptions(bool IncludePasswordFields = false, bool IncludeReadOnlyFields = true,
    PdfFormDataLimits? Limits = null);
public sealed record PdfFormValidationIssue(string FieldName, string Code, string Message);

public interface IPdfFormDataService
{
    Task<PdfFormData> ReadFormDataAsync(PdfSnapshot source, PdfFormDataReadOptions? options = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PdfFormValidationIssue>> ValidateFormDataAsync(PdfSnapshot source, CancellationToken cancellationToken = default);
}

public abstract record PdfFormDataEdit(string Description) : PdfEditOperation(PdfCapability.Forms, Description);
public sealed record ImportFormData(PdfFormData Data, bool IgnoreUnknownFields = false, bool IgnoreReadOnlyFields = false)
    : PdfFormDataEdit("Import form data");
public sealed record SetFormDefaults(PdfFormData Data) : PdfFormDataEdit("Set form default values");
public sealed record ResetFormData : PdfFormDataEdit
{
    public ResetFormData(IEnumerable<string>? names = null, bool skipReadOnly = true) : base("Reset form values")
    {
        AllFields = names is null;
        Names = names?.Take(100_001).ToImmutableArray() ?? [];
        if (Names.Length > 100_000 || Names.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Invalid reset selection.", nameof(names));
        SkipReadOnly = skipReadOnly;
    }
    public bool AllFields { get; }
    public ImmutableArray<string> Names { get; }
    public bool SkipReadOnly { get; }
}
public sealed record ConfigureChoiceField(string Name, bool MultipleSelection) : PdfFormDataEdit("Configure choice selection");
public sealed record ConfigureTextField(string Name, bool? Multiline = null, int? MaximumLength = null,
    bool? Password = null, bool? NoExport = null) : PdfFormDataEdit("Configure text field");
