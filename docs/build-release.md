# Build, test and release

## Local checks

Use the .NET 10 SDK and install `wasm-tools`. Shared libraries target .NET 8; Uno uses .NET 10. The native Uno sample includes macOS only; its browser head and the original Avalonia/WPF applications cover the other supported scenarios.

```sh
dotnet workload install wasm-tools
dotnet restore ProPDF.slnx -p:Configuration=Release
python scripts/fetch-uno-notices.py
dotnet build ProPDF.slnx -c Release --no-restore
dotnet test tests/ProPDF.Tests -c Release --no-build
dotnet run --project tests/ProPDF.Avalonia.Smoke -c Release --no-build
# Windows:
dotnet run --project tests/ProPDF.Wpf.Smoke -c Release --no-build
dotnet pack ProPDF.slnx -c Release --no-build -o artifacts/packages
pwsh scripts/verify-packages.ps1 -PackageDirectory artifacts/packages
python -m pip install -r docs/requirements.txt
python -m mkdocs build --strict
```

Restore/build configurations must agree. The solution includes both Uno sample targets, so license auditing can require every project's restored graph. No `--allow-partial` exception is used in CI.

## Validation

Linux, Windows and macOS build the full solution with warnings as errors. Tests cover independently reopened native edits, redaction text/streams/pixels, forms, encryption/signatures, selection transactions, file handoffs and resource ownership. Avalonia uses real Skia headless rendering; WPF renders in software on Windows. These are regression tests, not comprehensive accessibility or physical-GPU qualification.

Nine NuGet packages and symbol archives are inspected, including README/XML documentation and notice propagation. Clean external consumers compile all three UI adapters and run packaged PDF editing/extraction/rendering without project-reference fallbacks. The Uno consumer disables implicit unrelated SDK dependencies just like the sample.

The separate Uno browser workflow publishes WebAssembly and runs Chromium against `/ProPDF/`. Tests use actual controls, pointer input, PDF edits, downloads and reopening. Runtime/test artifacts and screenshots are uploaded even on failure. Source-provenance and permissive-license gates remain mandatory.

## Notices and documentation

`fetch-uno-notices.py` obtains three legal documents missing from legacy package metadata and checks their exact reviewed Git-blob identities. Downloads are bounded, cached and fail on changes. It does not fetch source, fonts, secrets or arbitrary URLs. The full notices are included in `ProPDF.Uno`, propagated to consumers, and retained in the site. Packing refuses missing notices.

The site combines the app at `/ProPDF/`, documentation at `/ProPDF/docs/` and notices at `/ProPDF/licenses/`. Only the Uno workflow deploys; the documentation workflow builds without overwriting the app. Repository Pages must use GitHub Actions and permit deployment. No live site is asserted until the deployment and public URL are verified.

## Release operations

The guarded Release workflow supports non-publishing manual runs and immutable `v<SemVer>` tags. Configure a protected `nuget-release` environment, required reviewers, package-ID ownership and a scoped `NUGET_API_KEY` before publication. Tags must point to the exact checked-out commit reachable from main. Do not place credentials or commercial entitlements in source.

A release repeats validation, packages native samples and preserves dependency notices. NuGet packages are pushed in dependency order, followed by a GitHub release with packages, symbols, source, sample archives and checksums. Prerelease suffixes produce prereleases. Existing published assets are not overwritten. Native archives are unsigned/unnotarized, not installers.

NuGet publication across several IDs is not atomic. Repair partial failures and rerun the same immutable tag with duplicate skipping; never move tags or overwrite versions. Check actual run evidence before asserting publication or support. A green build is not full Acrobat parity, conformance certification, certified redaction or a performance benchmark.
