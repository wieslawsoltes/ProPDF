[CmdletBinding()]
param([string]$PackageDirectory = 'artifacts/packages')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$feed = (Resolve-Path $PackageDirectory).Path
$ids = @('ProPDF.Kernel','ProPDF.Core','ProPDF.Rendering.Skia','ProPDF.Engine.PdfPig','ProPDF.Editing','ProPDF.Presentation','ProPDF.Avalonia','ProPDF.Wpf','ProPDF.Uno')
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
        if ($id -eq 'ProPDF.Engine.PdfPig') {
            foreach ($notice in @('LICENSE.txt','NOTICE.txt','PROVENANCE.json','PATCHES.md')) {
                if (-not $zip.GetEntry("licenses/PdfPig.Skia/$notice")) { throw "Missing renderer legal/provenance asset: $notice" }
            }
            if (-not $zip.GetEntry('buildTransitive/ProPDF.Engine.PdfPig.targets')) { throw 'Missing renderer notice propagation.' }
            if ([string]$manifest.package.metadata.license.InnerText -ne 'MIT AND Apache-2.0') { throw 'Incorrect mixed-source adapter license expression.' }
        }
        if ($id -eq 'ProPDF.Uno') {
            foreach ($notice in @('Uno-Eventing-LICENSE.txt','Uno-ICU-LICENSE.txt','ICU-77-LICENSE.txt')) {
                if (-not $zip.GetEntry("licenses/Uno/$notice")) { throw "Missing Uno runtime notice: $notice" }
            }
            if (-not $zip.GetEntry('buildTransitive/ProPDF.Uno.targets')) { throw 'Missing Uno notice propagation.' }
        }
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
$previousNodeReuse = $env:MSBUILDDISABLENODEREUSE
$env:MSBUILDDISABLENODEREUSE = '1'
function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed: $LASTEXITCODE" }
}
function New-Consumer([string]$Name,[string]$Framework,[string[]]$References,[string]$Code,[bool]$Executable=$false,[bool]$Wpf=$false,[bool]$Uno=$false) {
    $directory = Join-Path $work $Name
    New-Item $directory -ItemType Directory | Out-Null
    $refs = ($References | ForEach-Object { "<PackageReference Include=`"$_`" Version=`"$version`" />" }) -join "`n"
    $native = if ($Executable) { '<PackageReference Include="SkiaSharp.NativeAssets.Linux.NoDependencies" Version="3.119.4" /><PackageReference Include="HarfBuzzSharp.NativeAssets.Linux" Version="8.3.1.3" />' } else { '' }
    $output = if ($Executable) { 'Exe' } else { 'Library' }
    $useWpf = if ($Wpf) { '<UseWPF>true</UseWPF>' } else { '' }
    $sdk = if ($Uno) { 'Uno.Sdk/6.7.30' } else { 'Microsoft.NET.Sdk' }
    $unoProperties = if ($Uno) { '<UnoFeatures>Skia;SkiaRenderer</UnoFeatures><DisableImplicitUnoPackages>true</DisableImplicitUnoPackages><SkiaSharpVersion>3.119.4</SkiaSharpVersion><GenerateLibraryLayout>true</GenerateLibraryLayout>' } else { '' }
    @"
<Project Sdk="$sdk">
<PropertyGroup><TargetFramework>$Framework</TargetFramework><OutputType>$output</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors><EnableWindowsTargeting>true</EnableWindowsTargeting><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally><UseSharedCompilation>false</UseSharedCompilation>$useWpf $unoProperties</PropertyGroup>
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
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;
var backend = new PdfPigBackend();
var editor = new ManagedPdfEditor(backend);
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
    $engine = New-Consumer -Name EngineConsumer -Framework net8.0 -References $ids[0..5] -Code $code -Executable $true
    $avalonia = New-Consumer -Name AvaloniaConsumer -Framework net8.0 -References @('ProPDF.Avalonia') -Code 'public static class Consumer { public static ProPDF.Avalonia.PdfEditor Create(ProPDF.Presentation.PdfEditorContext c) => new() { Context = c }; }'
    $wpf = New-Consumer -Name WpfConsumer -Framework net8.0-windows -References @('ProPDF.Wpf') -Wpf $true -Code 'public static class Consumer { public static ProPDF.Wpf.PdfEditor Create(ProPDF.Presentation.PdfEditorContext c) => new() { Context = c }; }'
    $uno = New-Consumer -Name UnoConsumer -Framework net10.0 -References @('ProPDF.Uno') -Uno $true -Code 'public static class Consumer { public static ProPDF.Uno.PdfEditor Create(ProPDF.Presentation.PdfEditorContext c) => new() { Context = c }; public static ProPDF.Uno.PdfView CreateView() => new(); }'
    foreach ($project in @($engine,$avalonia,$wpf,$uno)) {
        Invoke-DotNet -Arguments @('restore',$project,'-p:Configuration=Release','--disable-build-servers','--configfile',(Join-Path $work 'NuGet.Config'))
        Invoke-DotNet -Arguments @('build',$project,'-c','Release','--no-restore','--disable-build-servers','-p:UseSharedCompilation=false')
    }
    Invoke-DotNet -Arguments @('run','--project',$engine,'-c','Release','--no-build')
    if (@(Get-ChildItem (Join-Path $work 'EngineConsumer/bin') -Recurse -Filter NOTICE.txt | Where-Object { $_.DirectoryName -match 'PdfPig.Skia' }).Count -eq 0) { throw 'Packaged renderer notices were not copied to consumer output.' }
    if (@(Get-ChildItem (Join-Path $work 'UnoConsumer/bin') -Recurse -Filter ICU-77-LICENSE.txt).Count -eq 0) { throw 'Packaged Uno runtime notices were not copied to consumer output.' }
    Write-Host "PASS: all nine standalone packages at $version."
} finally {
    $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse
    for ($attempt = 0; $attempt -lt 5 -and (Test-Path $work); $attempt++) {
        try { Remove-Item $work -Recurse -Force -ErrorAction Stop }
        catch {
            if ($attempt -eq 4) { Write-Warning "Temporary consumer directory remains locked: $work. Validation failures remain fatal; only cleanup is best-effort." }
            else { Start-Sleep -Milliseconds (200 * ($attempt + 1)) }
        }
    }
}
