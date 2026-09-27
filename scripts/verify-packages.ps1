[CmdletBinding()]
param([string]$PackageDirectory = 'artifacts/packages')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$feed = (Resolve-Path $PackageDirectory).Path
$ids = @('ProPDF.Core','ProPDF.Rendering.Skia','ProPDF.Engine.PdfPig','ProPDF.Editing.iText','ProPDF.Presentation','ProPDF.Avalonia','ProPDF.Wpf')
$packages = @(Get-ChildItem $feed -Filter '*.nupkg')
if ($packages.Count -ne $ids.Count) { throw "Expected $($ids.Count) packages; found $($packages.Count)." }
$versions = @{}
foreach ($package in $packages) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entries = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
        if ($entries.Count -ne 1) { throw 'Invalid package manifest count.' }
        $reader = [System.IO.StreamReader]::new($entries[0].Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $id = [string]$manifest.package.metadata.id
        if ($id -notin $ids -or $versions.ContainsKey($id)) { throw "Unexpected or duplicate $id" }
        $versions[$id] = [string]$manifest.package.metadata.version
        if (-not $zip.GetEntry('README.md')) { throw "$id has no README." }
        if (@($zip.Entries | Where-Object { $_.FullName -like 'lib/*/*.dll' }).Count -eq 0) { throw "$id has no assembly." }
        if (@($zip.Entries | Where-Object { $_.FullName -like 'lib/*/*.xml' }).Count -eq 0) { throw "$id has no XML documentation." }
        if (-not (Test-Path (Join-Path $feed "$id.$($versions[$id]).snupkg"))) { throw "$id has no symbol package." }
    } finally { $zip.Dispose() }
}
if (@($versions.Values | Select-Object -Unique).Count -ne 1) { throw 'Package versions differ.' }
$version = $versions['ProPDF.Core']
$work = Join-Path ([System.IO.Path]::GetTempPath()) "propdf-consumer-$([guid]::NewGuid().ToString('N'))"
New-Item $work -ItemType Directory | Out-Null
function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed: $LASTEXITCODE" }
}
function New-Consumer([string]$Name,[string]$Framework,[string[]]$References,[string]$Code,[bool]$Executable=$false,[bool]$Wpf=$false) {
    $directory = Join-Path $work $Name
    New-Item $directory -ItemType Directory | Out-Null
    $refs = ($References | ForEach-Object { "<PackageReference Include=`"$_`" Version=`"$version`" />" }) -join "`n"
    $native = if ($Executable) { '<PackageReference Include="SkiaSharp.NativeAssets.Linux.NoDependencies" Version="3.119.4"/><PackageReference Include="HarfBuzzSharp.NativeAssets.Linux" Version="8.3.1.3"/><PackageReference Include="itext.pdfsweep" Version="5.0.7" NoWarn="NU1701"/>' } else { '' }
    $output = if ($Executable) { 'Exe' } else { 'Library' }
    $useWpf = if ($Wpf) { '<UseWPF>true</UseWPF>' } else { '' }
    @"
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>$Framework</TargetFramework><OutputType>$output</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors><EnableWindowsTargeting>true</EnableWindowsTargeting><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>$useWpf</PropertyGroup>
<ItemGroup>$refs $native</ItemGroup>
</Project>
"@ | Set-Content (Join-Path $directory "$Name.csproj") -Encoding utf8
    $Code | Set-Content (Join-Path $directory 'Program.cs') -Encoding utf8
    return (Join-Path $directory "$Name.csproj")
}
try {
    $escapedFeed = [System.Security.SecurityElement]::Escape($feed)
    $cache = [System.Security.SecurityElement]::Escape((Join-Path $work 'nuget-cache'))
    @"
<configuration>
<config><add key="globalPackagesFolder" value="$cache"/></config>
<packageSources><clear/><add key="ProPDF-artifacts" value="$escapedFeed"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><packageSource key="ProPDF-artifacts"><package pattern="ProPDF.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
"@ | Set-Content (Join-Path $work 'NuGet.Config') -Encoding utf8
    $code = @'
using ProPDF.Core;
using ProPDF.Editing.iText;
using ProPDF.Engine.PdfPig;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;
var backend = new PdfPigBackend();
var editor = new ITextPdfEditor(backend);
var document = await editor.CreateAsync();
document = await editor.ApplyAsync(document, new IPdfEditOperation[] { new AddText(1, new PdfPoint(30, 60), "NuGet consumption") });
if (!(await backend.GetPageTextAsync(document, 1)).Text.Contains("NuGet consumption")) throw new Exception("Packaged editor failed.");
await using var renderer = new SkiaPdfRenderer(backend);
using var tile = await renderer.RenderTileAsync(document, new SkiaTileRequest(1, new PdfRect(0, 0, 100, 100), 1));
if (tile.Image.Width != 100) throw new Exception("Packaged rendering failed.");
var session = new PdfSession(backend, editor);
await using var viewport = new PdfViewportController(session, renderer, backend);
Console.WriteLine("PASS: external NuGet editor, extraction and native Skia rendering.");
'@
    $engine = New-Consumer -Name EngineConsumer -Framework net8.0 -References $ids[0..4] -Code $code -Executable $true
    $avalonia = New-Consumer -Name AvaloniaConsumer -Framework net8.0 -References @('ProPDF.Avalonia') -Code 'public static class Consumer { public static ProPDF.Avalonia.PdfEditor Create(ProPDF.Presentation.PdfEditorContext c) => new() { Context = c }; }'
    $wpf = New-Consumer -Name WpfConsumer -Framework net8.0-windows -References @('ProPDF.Wpf') -Wpf $true -Code 'public static class Consumer { public static ProPDF.Wpf.PdfEditor Create(ProPDF.Presentation.PdfEditorContext c) => new() { Context = c }; }'
    foreach ($project in @($engine,$avalonia,$wpf)) {
        Invoke-DotNet -Arguments @('restore',$project,'--configfile',(Join-Path $work 'NuGet.Config'))
        Invoke-DotNet -Arguments @('build',$project,'-c','Release','--no-restore')
    }
    Invoke-DotNet -Arguments @('run','--project',$engine,'-c','Release','--no-build')
    Write-Host "PASS: all seven standalone packages at $version."
} finally { if (Test-Path $work) { Remove-Item $work -Recurse -Force } }
