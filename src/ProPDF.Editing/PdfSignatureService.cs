using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ProPDF.Core;
using ProPDF.Kernel;
using static ProPDF.Kernel.PdfValues;

namespace ProPDF.Editing;

/// <summary>Produces detached CMS bytes. Keys, credentials and provider lifetime remain host-owned.</summary>
public interface IPdfDetachedSignatureProvider
{
    byte[] SignDetached(ReadOnlyMemory<byte> content, IReadOnlyList<X509Certificate2> certificateChain, CancellationToken cancellationToken = default);
}

/// <summary>Uses a host-owned RSA key without exporting or disposing it; supports platform and hardware-backed RSA implementations.</summary>
public sealed class DotNetRsaSignature(RSA key) : IPdfDetachedSignatureProvider
{
    private readonly RSA _key = key ?? throw new ArgumentNullException(nameof(key));
    public byte[] SignDetached(ReadOnlyMemory<byte> content, IReadOnlyList<X509Certificate2> certificateChain, CancellationToken cancellationToken = default) =>
        DotNetCms.Sign(content, certificateChain, _key, cancellationToken);
}

/// <summary>ECDSA counterpart to the RSA provider. The caller owns the key and certificates.</summary>
public sealed class DotNetEcdsaSignature(ECDsa key) : IPdfDetachedSignatureProvider
{
    private readonly ECDsa _key = key ?? throw new ArgumentNullException(nameof(key));
    public byte[] SignDetached(ReadOnlyMemory<byte> content, IReadOnlyList<X509Certificate2> certificateChain, CancellationToken cancellationToken = default) =>
        DotNetCms.Sign(content, certificateChain, _key, cancellationToken);
}

internal static class DotNetCms
{
    public const string Sha256Oid = "2.16.840.1.101.3.4.2.1";
    public const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";
    public static byte[] Sign(ReadOnlyMemory<byte> content, IReadOnlyList<X509Certificate2> chain, AsymmetricAlgorithm key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (chain.Count is < 1 or > 32) throw new ArgumentException("Supply a leaf-first certificate chain.", nameof(chain));
        var cms = new SignedCms(new ContentInfo(content.ToArray()), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, chain[0])
        {
            PrivateKey = key, DigestAlgorithm = new Oid(Sha256Oid), IncludeOption = X509IncludeOption.EndCertOnly
        };
        for (var i = 1; i < chain.Count; i++) signer.Certificates.Add(chain[i]);
        // ESS SigningCertificateV2 binds the signer to its certificate using SHA-256, as required for CAdES baseline signatures.
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence(); writer.PushSequence(); writer.PushSequence(); writer.WriteOctetString(SHA256.HashData(chain[0].RawData));
        writer.PopSequence(); writer.PopSequence(); writer.PopSequence();
        signer.SignedAttributes.Add(new AsnEncodedData(new Oid(SigningCertificateV2Oid), writer.Encode()));
        signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
        cms.ComputeSignature(signer, silent: true); token.ThrowIfCancellationRequested(); return cms.Encode();
    }
}

public sealed record PdfSignatureIntegrity(string FieldName, bool CryptographicallyValid, bool CoversCurrentDocument,
    int SignedRevision, int TotalRevisions, string? Error);

/// <summary>Owned PDF byte-range and revision handling, backed by .NET's permissively licensed CMS implementation. No trust-chain or revocation verdict is implied.</summary>
public sealed class PdfSignatureService
{
    private readonly IPdfDocumentLoader _validator;
    private readonly int _maximumOutputBytes;
    public PdfSignatureService(IPdfDocumentLoader? validator = null, long maximumOutputBytes = 256L * 1024 * 1024)
    {
        if (maximumOutputBytes is < 1024 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maximumOutputBytes));
        _validator = validator ?? new ManagedPdfLoader(); _maximumOutputBytes = (int)maximumOutputBytes;
    }

    public async Task<PdfSnapshot> SignAsync(PdfSnapshot source, IPdfDetachedSignatureProvider signature,
        IEnumerable<X509Certificate2> certificateChain, string fieldName, string reason = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(signature); ArgumentNullException.ThrowIfNull(certificateChain);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        if (fieldName.Length > 512 || fieldName.Any(char.IsControl) || fieldName.Contains('.')) throw new ArgumentException("Use a nonempty flat signature-field name.");
        if (reason.Length > 16_384) throw new ArgumentOutOfRangeException(nameof(reason));
        var chain = certificateChain.ToArray(); if (chain.Length is < 1 or > 32) throw new ArgumentException("Supply a leaf-first certificate chain.");
        var bytes = await Task.Run(() =>
        {
            using var stream = source.OpenRead(); using var buffer = new MemoryStream(); stream.CopyTo(buffer);
            var limits = new PdfReadLimits(MaximumFileBytes: _maximumOutputBytes);
            var file = PdfFile.Open(buffer.ToArray(), source.GetPassword(), limits, cancellationToken); var graph = new PdfGraph(file, 100_000);
            if (!file.IsOwnerAuthorized) throw new UnauthorizedAccessException("Signing requires owner authorization.");
            if (file.Catalog.Contains("Perms") || file.EnumerateObjects().Any(item => item.Value is PdfDictionary d && d.Name("TransformMethod") is "DocMDP" or "FieldMDP"))
                throw new NotSupportedException("Certified/field-locked signatures require a dedicated modification-policy evaluator and are not signed implicitly.");
            var form = graph.EnsureDictionary(file.Catalog, "AcroForm");
            if (form.Contains("XFA")) throw new NotSupportedException("XFA signing is not supported by this service.");
            var existing = ManagedPdfEditor.Fields(graph).SingleOrDefault(field => field.Name == fieldName);
            if (existing is not null && (existing.Effective.Name("FT") != "Sig" || existing.Dictionary["V"] is not PdfNull || existing.Dictionary.Contains("Lock") || ((int)existing.Effective.Number("Ff") & 1) != 0))
                throw new InvalidOperationException("The field is occupied, not a signature field, read-only, or carries a lock policy.");
            const int reservedBytes = 32_768;
            var signatureDictionary = Dictionary(("Type", new PdfName("Sig")), ("Filter", new PdfName("Adobe.PPKLite")), ("SubFilter", new PdfName("ETSI.CAdES.detached")),
                ("ByteRange", new PdfArray(new PdfNumber(0L), new PdfNumber(long.MaxValue), new PdfNumber(long.MaxValue), new PdfNumber(long.MaxValue))),
                ("Contents", new PdfString(new byte[reservedBytes])), ("Reason", new PdfString(reason)),
                ("M", new PdfString("D:" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z")));
            var signatureReference = file.Add(signatureDictionary);
            if (existing is not null) existing.Dictionary["V"] = signatureReference;
            else
            {
                var fields = graph.OptionalArray(form["Fields"]); form["Fields"] = fields;
                fields.Items.Add(file.Add(Dictionary(("FT", new PdfName("Sig")), ("T", new PdfString(fieldName)), ("V", signatureReference))));
            }
            form["SigFlags"] = new PdfNumber(3L);
            var result = file.Save(_maximumOutputBytes, incremental: true);
            var written = PdfFile.Open(result, source.GetPassword(), limits, cancellationToken);
            var objectOffset = checked((int)written.GetObjectOffset(signatureReference));
            // These tokens belong to a newly allocated, canonical signature dictionary, not a scan through arbitrary PDF objects.
            var rangeTemplate = Encoding.ASCII.GetBytes("/ByteRange " + Encoding.ASCII.GetString(PdfObjectWriter.Serialize(signatureDictionary["ByteRange"])));
            var rangeRelative = result.AsSpan(objectOffset).IndexOf(rangeTemplate);
            var contentsPrefix = "/Contents "u8;
            var contentsRelative = result.AsSpan(objectOffset).IndexOf(contentsPrefix);
            if (rangeRelative < 0 || contentsRelative < 0) throw new InvalidDataException("Signature reservation is missing.");
            var rangeOffset = objectOffset + rangeRelative; var gapStart = objectOffset + contentsRelative + contentsPrefix.Length;
            var gapEnd = checked(gapStart + reservedBytes * 2 + 2);
            if (gapEnd > result.Length || result[gapStart] != '<' || result[gapEnd - 1] != '>') throw new InvalidDataException("Invalid signature content reservation.");
            var ranges = $"/ByteRange [0 {gapStart} {gapEnd} {result.Length - gapEnd}]";
            if (ranges.Length > rangeTemplate.Length) throw new InvalidDataException("Signature range reservation is too small.");
            Encoding.ASCII.GetBytes(ranges.PadRight(rangeTemplate.Length)).CopyTo(result, rangeOffset);
            var signedContent = JoinRanges(result, gapStart, gapEnd);
            var cms = signature.SignDetached(signedContent, Array.AsReadOnly(chain), cancellationToken);
            if (cms.Length == 0 || cms.Length > reservedBytes) throw new InvalidDataException("Detached CMS exceeds the 32-KiB signature reservation.");
            // Verify a custom provider before returning a PDF that advertises its signature.
            var verification = new SignedCms(new ContentInfo(signedContent), detached: true); verification.Decode(cms); verification.CheckSignature(verifySignatureOnly: true);
            if (verification.SignerInfos.Count != 1) throw new InvalidDataException("Exactly one CMS signer is required.");
            ValidateCadesCertificateBinding(verification.SignerInfos[0]);
            Encoding.ASCII.GetBytes(Convert.ToHexString(cms)).CopyTo(result, gapStart + 1);
            cancellationToken.ThrowIfCancellationRequested(); return result;
        }, cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(bytes, writable: false);
        return await _validator.OpenAsync(output, new PdfOpenOptions { Password = source.GetPassword(), MaximumBytes = _maximumOutputBytes }, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PdfSignatureIntegrity>> VerifyIntegrityAsync(PdfSnapshot source, CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<PdfSignatureIntegrity>>(() =>
    {
        using var input = source.OpenRead(); using var buffer = new MemoryStream(); input.CopyTo(buffer); var bytes = buffer.ToArray();
        var limits = new PdfReadLimits(MaximumFileBytes: _maximumOutputBytes);
        var file = PdfFile.Open(bytes, source.GetPassword(), limits, cancellationToken); var graph = new PdfGraph(file, 100_000);
        var signatures = ManagedPdfEditor.Fields(graph).Where(field => field.Effective.Name("FT") == "Sig" && graph.Resolve(field.Dictionary["V"]) is PdfDictionary).ToArray();
        if (signatures.Length > 128) throw new InvalidDataException("Signature verification count budget exceeded.");
        var results = new List<PdfSignatureIntegrity>();
        foreach (var field in signatures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var dictionary = graph.Dictionary(field.Dictionary["V"]);
                if (dictionary.Name("SubFilter") is not ("adbe.pkcs7.detached" or "ETSI.CAdES.detached")) throw new NotSupportedException("Unsupported signature subfilter.");
                var ranges = file.Array(dictionary["ByteRange"]);
                if (ranges.Count != 4) throw new InvalidDataException("Expected one excluded signature content range.");
                var values = ranges.Select(value => file.Resolve(value) is PdfNumber number ? number.Integer : throw new InvalidDataException("Invalid signature byte range.")).ToArray();
                if (values[0] != 0 || values[1] < 1 || values[2] <= values[1] || values[3] < 0 || values[2] > bytes.Length || values[3] > bytes.Length - values[2])
                    throw new InvalidDataException("Signature byte ranges are outside the document or overlap.");
                var start = checked((int)values[1]); var end = checked((int)values[2]); var signedEnd = checked((int)(values[2] + values[3]));
                if (end - start > 1_048_576 || bytes[start] != '<' || bytes[end - 1] != '>') throw new InvalidDataException("The unsigned gap is not a bounded hexadecimal signature token.");
                var reader = new PdfSyntaxReader(bytes, start, limits, cancellationToken, end);
                var gap = reader.ReadObject() as PdfString ?? throw new InvalidDataException("Invalid signature contents.");
                if (reader.Position != end || file.Resolve(dictionary["Contents"]) is not PdfString contents || !gap.Bytes.SequenceEqual(contents.Bytes))
                    throw new InvalidDataException("Excluded signature token does not match this signature dictionary.");
                var encoded = contents.ToArray(); var asn = new AsnReader(encoded, AsnEncodingRules.DER);
                var cmsBytes = asn.ReadEncodedValue().ToArray();
                if (encoded.AsSpan(cmsBytes.Length).IndexOfAnyExcept((byte)0) >= 0) throw new InvalidDataException("Unexpected data after CMS signature.");
                var content = JoinRanges(bytes.AsSpan(0, signedEnd), start, end);
                var cms = new SignedCms(new ContentInfo(content), detached: true); cms.Decode(cmsBytes); cms.CheckSignature(verifySignatureOnly: true);
                if (cms.SignerInfos.Count != 1) throw new InvalidDataException("Expected one CMS signer.");
                if (dictionary.Name("SubFilter") == "ETSI.CAdES.detached") ValidateCadesCertificateBinding(cms.SignerInfos[0]);
                var revision = PdfFile.Open(bytes.AsSpan(0, signedEnd), source.GetPassword(), limits, cancellationToken).RevisionCount;
                results.Add(new PdfSignatureIntegrity(field.Name, true, signedEnd == bytes.Length, revision, file.RevisionCount, null));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { results.Add(new PdfSignatureIntegrity(field.Name, false, false, 0, file.RevisionCount, error.GetType().Name + ": " + error.Message)); }
        }
        return results.AsReadOnly();
    }, cancellationToken);

    private static byte[] JoinRanges(ReadOnlySpan<byte> bytes, int gapStart, int gapEnd)
    {
        var content = new byte[checked(gapStart + bytes.Length - gapEnd)]; bytes[..gapStart].CopyTo(content); bytes[gapEnd..].CopyTo(content.AsSpan(gapStart)); return content;
    }
    private static void ValidateCadesCertificateBinding(SignerInfo signer)
    {
        if (signer.DigestAlgorithm.Value is not (DotNetCms.Sha256Oid or "2.16.840.1.101.3.4.2.2" or "2.16.840.1.101.3.4.2.3"))
            throw new CryptographicException("Only SHA-256 or stronger SHA-2 CMS digests are accepted.");
        var attributes = signer.SignedAttributes.Cast<CryptographicAttributeObject>().Where(attribute => attribute.Oid.Value == DotNetCms.SigningCertificateV2Oid).ToArray();
        if (attributes.Length != 1 || attributes[0].Values.Count != 1 || signer.Certificate is null) throw new CryptographicException("Missing CAdES signing-certificate binding.");
        var reader = new AsnReader(attributes[0].Values[0].RawData, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence(); var certificates = sequence.ReadSequence(); var certificate = certificates.ReadSequence();
        if (certificate.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
        {
            var algorithm = certificate.ReadSequence();
            if (algorithm.ReadObjectIdentifier() != DotNetCms.Sha256Oid) throw new CryptographicException("Unsupported signing-certificate hash algorithm.");
            if (algorithm.HasData) algorithm.ReadNull(); algorithm.ThrowIfNotEmpty();
        }
        var hash = certificate.ReadOctetString();
        if (!CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(signer.Certificate.RawData))) throw new CryptographicException("CAdES certificate hash mismatch.");
        reader.ThrowIfNotEmpty();
    }
}
