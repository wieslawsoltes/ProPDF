# Licensing and engine choice

ProPDF uses replaceable adapters rather than claiming one library is universally the best PDF engine.

| Component | Role | License boundary |
| --- | --- | --- |
| ProPDF-authored code | Contracts, rendering orchestration, presentation and controls | MIT |
| PdfPig | Parsing and extraction | Apache-2.0 |
| PdfPig.Rendering.Skia | PDF-to-Skia interpretation | Apache-2.0 |
| SkiaSharp | Native drawing and images | MIT |
| Avalonia | Cross-platform UI | Review the exact upstream/package terms |
| iText | Optional editing, forms, encryption and signing | AGPL or commercial licensing |
| pdfSweep | Optional content cleanup | AGPL or appropriate commercial licensing |

The adapter's MIT license does not relicense iText or pdfSweep. A combined application must comply with applicable AGPL terms or obtain the required commercial licenses for the products used. No commercial entitlement, license key or bypass is included. Obtain licensing advice for the intended distribution/deployment model.

The UI, presentation and rendering packages do not depend on the optional editing adapter. Samples opt into it at their composition root. The combination preserves the requested SkiaSharp rendering path separately from editing, but does not guarantee complete Acrobat fidelity, every PDF feature or certification.

pdfSweep 5.0.7 exposes a .NET Framework assembly. The package-specific NU1701 exception is described in [Dependency compatibility](dependency-compatibility.md); successful tests of used paths do not prove universal runtime compatibility.

## Primary references

- [PdfPig](https://github.com/UglyToad/PdfPig)
- [PdfPig.Rendering.Skia](https://github.com/BobLd/PdfPig.Rendering.Skia)
- [SkiaSharp](https://github.com/mono/SkiaSharp)
- [Avalonia](https://github.com/AvaloniaUI/Avalonia)
- [iText licensing](https://itextpdf.com/how-buy/AGPLv3-license)
- [iText .NET API](https://api.itextpdf.com/iText/dotnet/9.7.0/)
- [pdfSweep API](https://api.itextpdf.com/pdfSweep/dotnet/latest/)
