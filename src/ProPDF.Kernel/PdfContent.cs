using System.Text;

namespace ProPDF.Kernel;

public sealed record PdfContentInstruction(string Operator, IReadOnlyList<PdfObject> Operands, int ByteOffset = 0, int ByteLength = 0);
public sealed record PdfContentLimits(int MaximumBytes = 64 * 1024 * 1024, int MaximumInstructions = 1_000_000, int MaximumOperands = 65_536);

/// <summary>PDF content tokenization without interpreting font, graphics or UI state. Inline images require a codec-aware reader and are rejected.</summary>
public static class PdfContent
{
    public static IReadOnlyList<PdfContentInstruction> Read(ReadOnlySpan<byte> bytes, PdfContentLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new PdfContentLimits();
        if (limits.MaximumBytes < 1 || limits.MaximumInstructions < 1 || limits.MaximumOperands < 1) throw new ArgumentOutOfRangeException(nameof(limits));
        if (bytes.Length > limits.MaximumBytes) throw new InvalidDataException("Content stream exceeds byte budget.");
        var data = bytes.ToArray(); var reader = new PdfSyntaxReader(data, cancellationToken: cancellationToken);
        var result = new List<PdfContentInstruction>(); var operands = new List<PdfObject>(); var start = 0;
        while (!reader.End)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operands.Count == 0) start = reader.Position;
            var first = data[reader.Position];
            var isObject = first is (byte)'/' or (byte)'(' or (byte)'<' or (byte)'[' or (byte)'+' or (byte)'-' or (byte)'.' || first is >= 48 and <= 57;
            if (!isObject)
            {
                var position = reader.Position; var word = reader.ReadKeyword(); reader.Position = position;
                isObject = word is "true" or "false" or "null";
            }
            if (isObject)
            {
                var operand = reader.ReadObject();
                if (operand is PdfReference) throw new InvalidDataException("Indirect references are forbidden in page content operands.");
                operands.Add(operand);
                if (operands.Count > limits.MaximumOperands) throw new InvalidDataException("Content operand budget exceeded.");
                continue;
            }
            var operation = reader.ReadKeyword();
            if (operation is "BI" or "ID" or "EI") throw new NotSupportedException("Inline image tokenization requires a dedicated bounded image decoder; use image XObjects instead.");
            if (operation.Length > 64 || operation.Any(c => c < 33 || c > 126)) throw new InvalidDataException("Invalid content operator.");
            result.Add(new PdfContentInstruction(operation, Array.AsReadOnly(operands.ToArray()), start, reader.Position - start)); operands.Clear();
            if (result.Count > limits.MaximumInstructions) throw new InvalidDataException("Content instruction budget exceeded.");
        }
        if (operands.Count != 0) throw new InvalidDataException("Dangling operands at end of PDF content.");
        return result.AsReadOnly();
    }
    public static byte[] Write(IEnumerable<PdfContentInstruction> instructions, int maximumBytes = 64 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        using var result = new PdfBoundedStream(maximumBytes, cancellationToken);
        foreach (var instruction in instructions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (instruction.Operator.Length is < 1 or > 64 || instruction.Operator.Any(c => c < 33 || c > 126 || PdfSyntaxReader.IsDelimiter((byte)c)))
                throw new ArgumentException("Invalid PDF content operator.");
            foreach (var operand in instruction.Operands) { PdfObjectWriter.Write(result, operand, 128); result.WriteByte(32); }
            result.Write(Encoding.ASCII.GetBytes(instruction.Operator)); result.WriteByte(10);
        }
        return result.ToArray();
    }
}
