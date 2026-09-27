using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ProPDF.Core;

/// <summary>Field-only XFDF and versioned JSON exchange. File references, actions and annotation data are never followed or imported.</summary>
public static class PdfFormDataSerializer
{
    public const string XfdfNamespace = "http://ns.adobe.com/xfdf/";

    public static async Task<PdfFormData> ReadAsync(Stream source, PdfFormDataFormat format, PdfFormDataLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        var settings = limits ?? new PdfFormDataLimits(); settings.Validate();
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        var bytes = await PdfStreams.ReadBoundedAsync(source, settings.MaximumBytes, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => format == PdfFormDataFormat.Json ? ReadJson(bytes, settings, cancellationToken) :
            ReadXfdf(bytes, settings, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public static PdfBinaryAsset Write(PdfFormData data, PdfFormDataFormat format, PdfFormDataLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var settings = limits ?? new PdfFormDataLimits(); settings.Validate();
        _ = new PdfFormData(data.Fields, settings);
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        using var output = new BoundedOutput(settings.MaximumBytes, cancellationToken);
        if (format == PdfFormDataFormat.Json)
        {
            using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject(); writer.WriteString("format", "ProPDF.FormData"); writer.WriteNumber("version", 1);
            writer.WriteStartObject("fields");
            foreach (var field in data.Fields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartArray(field.Name);
                foreach (var value in field.Values) writer.WriteStringValue(value);
                writer.WriteEndArray();
            }
            writer.WriteEndObject(); writer.WriteEndObject(); writer.Flush();
        }
        else
        {
            XNamespace ns = XfdfNamespace;
            var fields = new XElement(ns + "fields");
            var nodes = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var field in data.Fields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = fields;
                var path = "";
                foreach (var part in field.Name.Split('.'))
                {
                    path = path.Length == 0 ? part : path + "." + part;
                    if (!nodes.TryGetValue(path, out var element))
                    {
                        element = new XElement(ns + "field", new XAttribute("name", part));
                        nodes.Add(path, element); parent.Add(element);
                    }
                    parent = element;
                }
                foreach (var value in field.Values) parent.Add(new XElement(ns + "value", value));
            }
            var document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(ns + "xfdf", fields));
            using var writer = XmlWriter.Create(output, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false, CheckCharacters = true,
                NewLineHandling = NewLineHandling.Entitize
            });
            document.Save(writer); writer.Flush();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new PdfBinaryAsset(output.ToArray(), settings.MaximumBytes);
    }

    public static Task SaveAsync(string path, PdfFormData data, PdfFormDataFormat format, PdfFormDataLimits? limits = null,
        CancellationToken cancellationToken = default) =>
        PdfFileOutput.SaveAssetAsync(path, Write(data, format, limits, cancellationToken), cancellationToken);

    private static PdfFormData ReadJson(byte[] bytes, PdfFormDataLimits settings, CancellationToken token)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = settings.MaximumDepth, CommentHandling = JsonCommentHandling.Disallow });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != "ProPDF.FormData" ||
            !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1 ||
            !root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Unsupported form-data JSON schema/version.");
        if (root.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidDataException("Duplicate JSON root properties are not allowed.");
        var result = new List<PdfFormValue>();
        foreach (var field in fields.EnumerateObject())
        {
            token.ThrowIfCancellationRequested();
            if (result.Count == settings.MaximumFields) throw new InvalidDataException("Form data exceeds its field limit.");
            string[] values;
            if (field.Value.ValueKind == JsonValueKind.String) values = [field.Value.GetString()!];
            else if (field.Value.ValueKind == JsonValueKind.Array)
            {
                if (field.Value.GetArrayLength() > settings.MaximumValuesPerField) throw new InvalidDataException("Too many field values.");
                values = field.Value.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! :
                    throw new InvalidDataException("Form values must be strings.")).ToArray();
            }
            else throw new InvalidDataException("Form values must be strings or string arrays.");
            result.Add(new PdfFormValue(field.Name, values));
        }
        return new PdfFormData(result, settings);
    }

    private static PdfFormData ReadXfdf(byte[] bytes, PdfFormDataLimits settings, CancellationToken token)
    {
        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = false,
            MaxCharactersInDocument = settings.MaximumBytes, MaxCharactersFromEntities = 0,
            IgnoreComments = true, IgnoreProcessingInstructions = true
        };
        using var input = new MemoryStream(bytes, false);
        // Check XML depth before allocating a DOM, including ignored annotation/file-reference sections.
        using (var check = XmlReader.Create(input, readerSettings))
            while (check.Read())
            {
                token.ThrowIfCancellationRequested();
                if (check.Depth > settings.MaximumDepth + 3) throw new InvalidDataException("XFDF XML depth exceeds its limit.");
            }
        input.Position = 0;
        using var reader = XmlReader.Create(input, readerSettings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException("Missing XFDF root.");
        // Established producers also emit unqualified XFDF. Accept it without resolving any external resource.
        if (root.Name.LocalName != "xfdf" || root.Name.NamespaceName is not ("" or XfdfNamespace))
            throw new InvalidDataException("Invalid XFDF root/namespace.");
        var ns = root.Name.Namespace;
        var containers = root.Elements(ns + "fields").ToArray();
        if (containers.Length > 1) throw new InvalidDataException("XFDF must not contain multiple fields containers.");
        if (containers.Length == 0) return new PdfFormData(Array.Empty<PdfFormValue>(), settings);
        var result = new List<PdfFormValue>();
        var stack = new Stack<(XElement Field, string Parent, int Depth)>();
        foreach (var field in containers[0].Elements().Reverse()) stack.Push((field, "", 1));
        var nodes = 0;
        while (stack.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (++nodes > settings.MaximumFields * settings.MaximumDepth || item.Depth > settings.MaximumDepth)
                throw new InvalidDataException("XFDF tree exceeds its node/depth budget.");
            if (item.Field.Name != ns + "field") throw new InvalidDataException("Unsupported XFDF field element.");
            var name = (string?)item.Field.Attribute("name");
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("XFDF field has no name.");
            var fullName = item.Parent.Length == 0 ? name : item.Parent + "." + name;
            if (fullName.Length > settings.MaximumNameCharacters) throw new InvalidDataException("XFDF field name is too long.");
            var values = new List<string>(); var children = new List<XElement>();
            foreach (var child in item.Field.Elements())
            {
                if (child.Name == ns + "value")
                {
                    if (child.HasElements || values.Count == settings.MaximumValuesPerField) throw new InvalidDataException("Unsupported or oversized XFDF value.");
                    values.Add(child.Value);
                }
                else if (child.Name == ns + "field") children.Add(child);
                else throw new InvalidDataException("Only plain field values and nested fields are supported; rich text is not imported.");
            }
            if (values.Count > 0 || children.Count == 0)
            {
                if (result.Count == settings.MaximumFields) throw new InvalidDataException("XFDF field limit exceeded.");
                result.Add(new PdfFormValue(fullName, values));
            }
            for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], fullName, item.Depth + 1));
        }
        return new PdfFormData(result, settings);
    }

    private sealed class BoundedOutput(int maximumBytes, CancellationToken token) : MemoryStream
    {
        private void Check(int count)
        {
            token.ThrowIfCancellationRequested();
            if (Position + count > maximumBytes) throw new InvalidDataException("Serialized form data exceeds its byte limit.");
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
}
