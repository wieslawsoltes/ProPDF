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
# Uno library layout invokes incremental reference builds: do not use --no-build here.
dotnet pack ProPDF.slnx -c Release --no-restore -o artifacts/packages
pwsh scripts/verify-packages.ps1 -PackageDirectory artifacts/packages
python -m pip install -r docs/requirements.txt
python -m mkdocs build --strict
```

Restore and build configurations must agree. A complete local build includes both Uno sample heads. For native-only development, pass `-p:ProPDFBuildBrowser=false` consistently to restore, build and pack; no WebAssembly workload is needed for that mode. This does not validate the browser application.

## Validation

Linux, Windows and macOS validate the nine libraries, native sample heads and tests with warnings as errors. The native matrix sets `ProPDFBuildBrowser=false` to avoid installing and compiling the same WebAssembly toolchain three times. It audits all sixteen project graphs in that configuration without a partial-audit exception. The separate mandatory Uno workflow restores both sample heads, audits the full browser/desktop graph, publishes WebAssembly and tests the actual application in Chromium.

C# tests cover independently reopened edits, redaction text/streams/pixels, forms, encryption/signatures, selection transactions, file handoffs and resource ownership. Avalonia uses actual Skia headless rendering; WPF renders in software on Windows. These are regression checks, not comprehensive accessibility or physical-GPU qualification.

Nine NuGet libraries and symbols are inspected, including README, XML documentation and notices. Clean external consumers compile all three UI adapters and run packaged PDF editing/extraction/rendering without project-reference fallbacks. Uno's library-layout target requires incremental builds during packing; `--no-restore` reuses the previously audited graph while permitting those builds. The Uno consumer disables implicit unrelated SDK dependencies, just like the sample.

Browser tests use bound controls, real pointer input, PDF edits, downloads, independent output-pixel checks and reopening. Each interop request has a deadline; logs, progress and failure screenshots are retained rather than hanging indefinitely. These test diagnostics are sample-only, not in the reusable control package. Source-provenance and permissive-license checks remain mandatory.

## Notices and documentation

`fetch-uno-notices.py` obtains three legal documents missing from legacy package metadata and checks their exact reviewed Git-blob identities. Downloads are bounded, cached and fail on changes. It does not fetch code, fonts, secrets or arbitrary URLs. Full notices are included in `ProPDF.Uno`, propagated to consumers and retained in the website. Packing refuses missing notices.

The site combines the app at `/ProPDF/`, documentation at `/ProPDF/docs/` and notices at `/ProPDF/licenses/`. Only **Uno browser and Pages** deploys; Documentation builds without overwriting the app. Repository Pages must use GitHub Actions and permit deployment. No live site is asserted until the deployment and public URL are verified. See [Deployment prerequisites](deployment-setup.md).

## Release operations

The guarded Release workflow supports nonpublishing manual runs and immutable `v<SemVer>` tags. Configure a protected `nuget-release` environment, required reviewers, package-ID ownership and a scoped `NUGET_API_KEY` before publication. Tags must identify the exact checked-out commit reachable from main. Never commit credentials or commercial entitlements.

Release repeats native validation, packages the available native samples and preserves dependency notices. NuGet packages are pushed in dependency order, followed by a GitHub release containing packages, symbols, source, sample archives and checksums. The Uno browser pipeline is a separate required qualification and deployment path. Prerelease suffixes produce prereleases. Existing published assets are not overwritten. Native archives are unsigned/unnotarized, not installers.

NuGet publication across several IDs is not atomic. Repair partial failures and rerun the same immutable tag with duplicate skipping; never move tags or overwrite versions. Check actual run evidence before claiming publication or support. A green build is not full Acrobat parity, conformance certification, certified redaction or a performance benchmark.

After deployment, the Uno workflow requires the public `build-info.json` to identify the exact merged commit, verifies documentation and the non-partial license inventory, then repeats the full browser editor/download/reopen/pixel suite against the live Pages URL. CDN propagation retries are bounded; a successful deploy action alone does not pass this additional gate. The verifier has seven offline Node regression tests.
