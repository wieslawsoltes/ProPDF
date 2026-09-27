# Backend migration and compatibility

The owned engine replaces the previous `ProPDF.Editing.iText` package with `ProPDF.Editing` and adds `ProPDF.Kernel`. Remove old iText/pdfSweep package references and all related NU1701 suppressions from application projects. No compatibility shim depends on the removed libraries.

| Previous API | Owned replacement |
| --- | --- |
| `using ProPDF.Editing.iText` | `using ProPDF.Editing` |
| `ITextPdfEditor` | `ManagedPdfEditor` |
| `ITextEditorOptions` | `ManagedPdfEditorOptions` |
| Vendor certificate wrapper collections | `IEnumerable<X509Certificate2>` |
| Vendor `IExternalSignature` | `IPdfDetachedSignatureProvider` |
| Vendor-dependent document opening | `ManagedPdfLoader` or optional independent `PdfPigBackend` |

The Core operation contracts, sessions, viewports, navigation and native UI controls are retained. Both sample applications use the owned editor by default. Signing has an intentional source-breaking change that removes vendor cryptographic types. Existing native form/page/navigation/export regression tests were migrated rather than discarded.

## Changed boundaries

The redactor now uses conservative owned content-group removal. It rejects unqualified content instead of falling back to the removed backend; intersecting text objects, paths and image/form invocations can be removed in full. This differs from exact glyph-level and partial-image editing. Standard-password opening currently accepts printable ASCII only; full international password preparation remains work. TrueType embedding is implemented, but CFF/collections/shaping are not.

Review [Owned engine](owned-engine.md), [Native editing](editing.md) and the [feature matrix](features.md) before upgrading applications handling unusual or sensitive files. An unchanged operation name does not imply exhaustive equivalence with the removed implementation.

All libraries target modern .NET 8; WPF targets `net8.0-windows`. Native Skia/HarfBuzz runtime assets must match the deployment platform. Cross-building WPF on Linux/macOS is supported; running WPF still requires Windows. Every release must pass clean external package-consumer validation and the [permissive license gate](licensing.md).
