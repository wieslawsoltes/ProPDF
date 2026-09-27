using System.Security.Cryptography;
using iText.Commons.Bouncycastle.Cert;
using iText.Kernel.Pdf;
using iText.Signatures;
using ProPDF.Core;

namespace ProPDF.Editing.iText;

/// <summary>SHA-256/RSA adapter for host-owned RSA keys, including platform-backed implementations. Does not own or export the key.</summary>
public sealed class DotNetRsaSignature(RSA key) : IExternalSignature
{
    private readonly RSA _key = key ?? throw new ArgumentNullException(nameof(key));
    public string GetDigestAlgorithmName() => "SHA-256";
    public string GetSignatureAlgorithmName() => "RSA";
    public ISignatureMechanismParams GetSignatureMechanismParameters() => null!;
    public byte[] Sign(byte[] message) => _key.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
}

public sealed record PdfSignatureIntegrity(string FieldName, bool CryptographicallyValid, bool CoversCurrentDocument,
    int SignedRevision, int TotalRevisions, string? Error);

/// <summary>Detached CAdES signing and cryptographic integrity checks. Certificate trust, revocation and PAdES-LTV validation are not implied.</summary>
public sealed class PdfSignatureService(IPdfDocumentLoader validator, long maximumOutputBytes = 256L * 1024 * 1024)
{
    public async Task<PdfSnapshot> SignAsync(PdfSnapshot source, IExternalSignature signature, IEnumerable<IX509Certificate> certificateChain,
        string fieldName, string reason = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        var chain = certificateChain.ToArray();
        if (chain.Length is < 1 or > 32) throw new ArgumentException("Supply a leaf-first certificate chain.", nameof(certificateChain));
        if (signature.GetDigestAlgorithmName() is not ("SHA-256" or "SHA-384" or "SHA-512"))
            throw new NotSupportedException("Only SHA-256 or stronger SHA-2 signatures are accepted.");
        var bytes = await Task.Run(() =>
        {
            using var input = source.OpenRead();
            using var output = new BoundedPdfOutput(maximumOutputBytes, cancellationToken);
            using var reader = ITextPdfEditor.CreateReader(input, source.GetPassword());
            var signer = new PdfSigner(reader, output, new StampingProperties().UseAppendMode());
            try
            {
                var document = signer.GetDocument();
                if (!reader.IsOpenedWithFullPermission()) throw new UnauthorizedAccessException("Signing requires owner-authorized access.");
                if (document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Perms)?.ContainsKey(PdfName.DocMDP) == true)
                    throw new NotSupportedException("Signing certified documents requires DocMDP policy qualification and is disabled.");
                if (new SignatureUtil(document).GetSignatureNames().Contains(fieldName))
                    throw new ArgumentException("A signature already occupies that field.", nameof(fieldName));
                signer.SetSignerProperties(new SignerProperties().SetFieldName(fieldName).SetReason(reason));
                cancellationToken.ThrowIfCancellationRequested();
                signer.SignDetached(signature, chain, null, null, null, 0, PdfSigner.CryptoStandard.CADES);
                cancellationToken.ThrowIfCancellationRequested();
                return output.ToArray();
            }
            finally
            {
                if (!signer.GetDocument().IsClosed()) signer.GetDocument().Close();
            }
        }, cancellationToken).ConfigureAwait(false);
        using var result = new MemoryStream(bytes, writable: false);
        return await validator.OpenAsync(result, new PdfOpenOptions { Password = source.GetPassword(), MaximumBytes = maximumOutputBytes }, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PdfSignatureIntegrity>> VerifyIntegrityAsync(PdfSnapshot source, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<PdfSignatureIntegrity>>(() =>
        {
            using var input = source.OpenRead();
            using var document = new PdfDocument(ITextPdfEditor.CreateReader(input, source.GetPassword()));
            var utility = new SignatureUtil(document);
            var results = new List<PdfSignatureIntegrity>();
            foreach (var name in utility.GetSignatureNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var signature = utility.ReadSignatureData(name);
                    results.Add(new PdfSignatureIntegrity(name, signature.VerifySignatureIntegrityAndAuthenticity(),
                        utility.SignatureCoversWholeDocument(name), utility.GetRevision(name), utility.GetTotalRevisions(), null));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    results.Add(new PdfSignatureIntegrity(name, false, false, 0, utility.GetTotalRevisions(), error.GetType().Name));
                }
            }
            return results.AsReadOnly();
        }, cancellationToken);
}
