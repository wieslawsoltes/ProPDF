# Dependency compatibility policy

Libraries target .NET 8 and build with the .NET 10 SDK. Dependency versions are centralized and pinned. Compiler and restore warnings are errors, with one narrowly scoped exception.

## pdfSweep 5.0.7

The upstream `itext.pdfsweep` 5.0.7 package publishes a `net461` assembly, not a .NET Standard target. NuGet therefore restores it in .NET Framework compatibility mode and emits NU1701. The editor and integration-test projects explicitly allow **only this package's NU1701** warning. This does not assert that every upstream API is compatible with modern .NET.

The editor's text/region redaction paths must pass Linux, Windows and macOS integration tests before merge. Image, mask and every codec combination need additional qualification; a successful restore is not proof of runtime compatibility. The issue cannot be solved by changing the target framework of the ProPDF adapter alone. Re-evaluate the exception when upstream publishes a modern target.

Consumers using warnings-as-errors may need the same direct package-scoped exception in the application composition project:

```xml
<PackageReference Include="itext.pdfsweep" Version="5.0.7" NoWarn="NU1701" />
```

Core, parsing, rendering, presentation and UI controls do not depend on pdfSweep. Applications that cannot accept this compatibility exception can use the viewer/control libraries without the iText editing adapter, or supply a different `IPdfEditor`.

Upstream package: https://www.nuget.org/packages/itext.pdfsweep/5.0.7
Upstream target declaration: https://github.com/itext/itext-pdfsweep-dotnet/blob/develop/itext/Directory.Build.props
