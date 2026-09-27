[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version, [string]$PackageDirectory = 'artifacts/packages')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') { throw 'Invalid version.' }
if ([string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)) { throw 'Configure NUGET_API_KEY in the protected nuget-release environment.' }
$ids = @('ProPDF.Kernel', 'ProPDF.Core', 'ProPDF.Rendering.Skia', 'ProPDF.Engine.PdfPig', 'ProPDF.Editing', 'ProPDF.Presentation', 'ProPDF.Avalonia', 'ProPDF.Wpf', 'ProPDF.Uno')
foreach ($id in $ids) {
    if (-not (Test-Path (Join-Path $PackageDirectory "$id.$Version.nupkg"))) { throw "Missing package $id $Version" }
}
# Publication of several NuGet IDs is not atomic; duplicate skipping permits recovery.
foreach ($id in $ids) {
    $package = Join-Path $PackageDirectory "$id.$Version.nupkg"
    & dotnet nuget push $package --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json --skip-duplicate
    if ($LASTEXITCODE -ne 0) { throw "Publication failed for $id. Repair configuration and rerun the same immutable tag." }
}
