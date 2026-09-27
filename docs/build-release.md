# Build, test and release

## Local validation

Use the .NET 10 SDK and Python 3.13. Libraries target .NET 8. WPF executes only on Windows; all projects cross-build on Linux/macOS.

```sh
dotnet restore ProPDF.slnx
dotnet build ProPDF.slnx -c Release --no-restore
dotnet test tests/ProPDF.Tests -c Release --no-build
dotnet run --project tests/ProPDF.Avalonia.Smoke -c Release --no-build
# Windows only:
dotnet run --project tests/ProPDF.Wpf.Smoke -c Release --no-build

dotnet pack ProPDF.slnx -c Release --no-build -o artifacts/packages
pwsh scripts/verify-packages.ps1 -PackageDirectory artifacts/packages
python -m pip install -r docs/requirements.txt
python -m mkdocs build --strict
python scripts/verify-renderer-source.py
```

## CI gates

Both build and release validation run `python scripts/audit-licenses.py` after restoring all projects. Unknown or restricted licenses fail; reviewed legacy declarations are exact-version/hash pinned. Negative tests cover rejection and audit completeness. License inventories and package notices accompany validation/release artifacts.


Build and test validates the full solution on Linux, Windows and macOS with warnings as errors. Engine tests independently reopen edited PDFs and exercise geometry, redaction text/decoded streams/pixels, forms, encryption/signatures, revisions and native resource lifetime.

Avalonia smoke validation uses real Skia rather than mock headless drawing. WPF uses software rendering on Windows. Both harnesses check native document pixels, command wiring, search, undo and teardown, then upload screenshots. These are regression checks, not physical-GPU, comprehensive visual or accessibility qualification.

Package consumers checks all eight NuGet libraries, symbols, README and XML docs. Clean projects outside the repository restore ProPDF packages from the artifact feed, compile both UI adapters and execute packaged editing/extraction/rendering. Project references cannot hide missing packaged dependencies.

Documentation builds Material for MkDocs with strict links/configuration. Workflows upload artifacts, but source configuration alone is not evidence of a successful run. Consult the exact commit's Actions status before merging.

## Documentation deployment

Only main deploys through the github-pages environment. Configure repository Settings → Pages → **GitHub Actions**. The intended URL is `https://wieslawsoltes.github.io/ProPDF/`; it is not asserted live until a deployment succeeds. PRs build the site but do not deploy it. Direct Python dependencies are pinned and the theme uses system fonts rather than external font requests.

## Release dry run

Manually run Release with a SemVer and **publish=false**. It validates the source/docs, packs libraries and produces self-contained Avalonia samples for Linux x64, Windows x64 and macOS arm64, plus WPF for Windows x64. Unix tar archives preserve executable permissions; Windows uses ZIP. Samples are unsigned/unnotarized and are not installers.

Dry runs do not publish to NuGet or create GitHub releases. GitHub manual dispatch may require the workflow to be present on the default branch first.

## Maintainer setup

Create the **nuget-release** environment with required reviewers and protected-tag restrictions. Configure a scoped `NUGET_API_KEY` for the intended package IDs and establish their NuGet ownership. Protect main and release tags. Run the permissive dependency audit and preserve bundled notices before distributing samples. No secrets or commercial PDF license keys are required or included.

After review/merge and successful CI, create an immutable `v<SemVer>` tag on a commit reachable from main. Publishing manual runs also require that exact existing tag to identify the checked-out commit. Invalid versions, mismatched tags and unreviewed branch commits are rejected.

Release repeats cross-platform build/test/headless/package-consumer validation and strict documentation builds. After protected environment approval it publishes NuGet dependencies in order, then creates the GitHub release with libraries, symbols, sample archives, source and SHA-256 checksums. Versions with prerelease suffixes are marked prereleases.

## Failure recovery

Publishing several NuGet IDs is not atomic. A failure may leave a subset published. Repair credentials/configuration and rerun the same immutable tag; duplicate skipping supports recovery. Do not move release tags or replace an existing version. Existing GitHub release assets are not silently overwritten.

A green build does not prove full Acrobat compatibility, PDF/A/PDF/UA conformance, sensitive-redaction safety or production performance. Record actual successful run links, versions and qualification evidence when releasing.


The source-pinned optional renderer is also checked against its committed source/notice hash inventory. Native release validation retains the original Apache-2.0 LICENSE/NOTICE and patch provenance; clean external package consumers verify the mixed license expression and propagated notices. This does not make the optional interpreter ProPDF-authored or replace an independent conformance/security audit.
