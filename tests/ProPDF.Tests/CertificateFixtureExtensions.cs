using iText.Commons.Bouncycastle.Cert;
using iText.Commons.Bouncycastle.X509;

namespace ProPDF.Tests;

internal static class CertificateFixtureExtensions
{
    // iText's portable parser exposes ReadAllCerts, unlike the underlying Bouncy Castle API.
    // A generated signing fixture must contain exactly one leaf certificate.
    public static IX509Certificate ReadCertificate(this IX509CertificateParser parser, byte[] der) =>
        parser.ReadAllCerts(der).Single();
}
