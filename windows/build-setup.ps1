param(
    [Parameter(Mandatory = $true)][string]$PayloadZip,
    [string]$Version = '0.7.0-preview',
    [string]$OutputPath,
    [string]$BuildDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Install .NET Framework 4.8 on Windows x64 before building Setup.' }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$') { throw 'Version must look like 0.5.0-preview.' }
$assemblyVersion = [regex]::Match($Version, '^\d+\.\d+\.\d+').Value + '.0'
$payloadPath = (Resolve-Path -LiteralPath $PayloadZip).ProviderPath
if (!$OutputPath) { $OutputPath = Join-Path (Split-Path -Parent $projectRoot) "IPMsg-Phone-Dock-v$Version-Setup.exe" }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (!$BuildDirectory) { $BuildDirectory = Join-Path (Split-Path -Parent (Split-Path -Parent $projectRoot)) ('work/dock-setup-build-' + [Guid]::NewGuid().ToString('N')) }
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
if (Test-Path -LiteralPath $BuildDirectory) { throw 'Choose a new, empty BuildDirectory. Existing build directories are preserved.' }
New-Item -ItemType Directory -Path $BuildDirectory | Out-Null
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null

$metadataPath = Join-Path $BuildDirectory 'BuildInfo.cs'
$metadata = @"
using System.Reflection;
[assembly: AssemblyTitle("IPMsg Dock Setup")]
[assembly: AssemblyProduct("IPMsg Dock")]
[assembly: AssemblyCompany("IPMsg contributors")]
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
[assembly: AssemblyInformationalVersion("$Version")]
namespace IPMsg.Dock.Setup { internal static class BuildInfo { internal const string Version = "$Version"; } }
"@
[IO.File]::WriteAllText($metadataPath, $metadata, (New-Object Text.UTF8Encoding($false)))
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'setup') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }) + $metadataPath
$common = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output', '/codepage:65001', '/warn:4', '/langversion:5',
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll',
    "/win32manifest:$projectRoot/setup/setup.manifest", "/win32icon:$projectRoot/assets/ipmsg.ico")
$uninstaller = Join-Path $BuildDirectory 'Uninstall.exe'
& $compiler $common '/define:UNINSTALLER' "/out:$uninstaller" $sources
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller compilation failed.' }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Assert-PayloadName([string]$Name) {
    if (!$Name -or $Name.Length -gt 220 -or $Name.Contains('\') -or $Name.StartsWith('/') -or [IO.Path]::IsPathRooted($Name)) { throw "Unsafe ZIP path: $Name" }
    foreach ($segment in $Name.Split('/')) {
        if (!$segment -or $segment -eq '.' -or $segment -eq '..' -or $segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment -match '[\x00-\x1f<>:"|?*]') { throw "Unsafe ZIP path: $Name" }
        $stem = $segment.Split('.')[0]
        if ($stem -match '^(?i:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$') { throw "Reserved ZIP path: $Name" }
    }
    if ($Name -ieq '.ipmsgdock-install.tsv' -or $Name -ieq 'Uninstall.exe') { throw "Payload uses a reserved installer file: $Name" }
}
function Get-StreamHash([IO.Stream]$Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Stream)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
$normalizedZip = Join-Path $BuildDirectory 'Payload.zip'
$manifestPath = Join-Path $BuildDirectory 'Payload.tsv'
$manifestLines = New-Object 'Collections.Generic.List[string]'
$manifestLines.Add('IPMSGDOCK-PAYLOAD-1')
$names = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$inputFile = [IO.File]::OpenRead($payloadPath)
$outputFile = New-Object IO.FileStream($normalizedZip, [IO.FileMode]::CreateNew)
$inputZip = $null
$outputZip = $null
try {
    $inputZip = New-Object IO.Compression.ZipArchive($inputFile, [IO.Compression.ZipArchiveMode]::Read, $false)
    $outputZip = New-Object IO.Compression.ZipArchive($outputFile, [IO.Compression.ZipArchiveMode]::Create, $false)
    $files = @($inputZip.Entries | Where-Object { !$_.FullName.EndsWith('/') })
    if ($files.Count -eq 0) { throw 'The payload ZIP is empty.' }
    $prefix = 'IPMsg-Phone-Dock/'
    $stripPrefix = @($files | Where-Object { !$_.FullName.StartsWith($prefix, [StringComparison]::Ordinal) }).Count -eq 0
    foreach ($source in @($inputZip.Entries | Sort-Object FullName)) {
        $original = $source.FullName
        $directory = $original.EndsWith('/')
        $name = $original
        if ($stripPrefix) {
            if ($original -eq $prefix) { continue }
            if (!$original.StartsWith($prefix, [StringComparison]::Ordinal)) { throw 'ZIP mixes the known application prefix with other entries.' }
            $name = $original.Substring($prefix.Length)
        }
        if ($directory) { $name = $name.Substring(0, $name.Length - 1) }
        Assert-PayloadName $name
        $unixType = ($source.ExternalAttributes -shr 16) -band 0xf000
        if ($unixType -eq 0xa000 -or ($source.ExternalAttributes -band 0x400) -ne 0) { throw "ZIP contains a link: $name" }
        if ($directory) { continue }
        if (!$names.Add($name)) { throw "ZIP contains a duplicate path: $name" }
        $entry = $outputZip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
        $sourceStream = $source.Open()
        $destinationStream = $entry.Open()
        try { $sourceStream.CopyTo($destinationStream) }
        finally { $destinationStream.Dispose(); $sourceStream.Dispose() }
        $sourceStream = $source.Open()
        try { $hash = Get-StreamHash $sourceStream }
        finally { $sourceStream.Dispose() }
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($name))
        $manifestLines.Add("F`t$hash`t$($source.Length)`t$encoded")
    }
    if (!$names.Contains('IPMsgDock.exe') -or !$names.Contains('runtime/scrcpy/scrcpy.exe')) { throw 'The payload must include IPMsgDock.exe and runtime/scrcpy/scrcpy.exe at its root or under IPMsg-Phone-Dock/.' }
    $uninstallEntry = $outputZip.CreateEntry('Uninstall.exe', [IO.Compression.CompressionLevel]::Optimal)
    $sourceStream = [IO.File]::OpenRead($uninstaller)
    $destinationStream = $uninstallEntry.Open()
    try { $sourceStream.CopyTo($destinationStream) }
    finally { $destinationStream.Dispose(); $sourceStream.Dispose() }
    $sourceStream = [IO.File]::OpenRead($uninstaller)
    try { $hash = Get-StreamHash $sourceStream }
    finally { $sourceStream.Dispose() }
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('Uninstall.exe'))
    $manifestLines.Add("F`t$hash`t$((Get-Item -LiteralPath $uninstaller).Length)`t$encoded")
}
finally {
    if ($outputZip) { $outputZip.Dispose() } else { $outputFile.Dispose() }
    if ($inputZip) { $inputZip.Dispose() } else { $inputFile.Dispose() }
}
[IO.File]::WriteAllLines($manifestPath, $manifestLines, (New-Object Text.UTF8Encoding($false)))
& $compiler $common "/resource:$normalizedZip,IPMsgDock.Setup.Payload.zip" "/resource:$manifestPath,IPMsgDock.Setup.Payload.tsv" "/out:$OutputPath" $sources
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
$sum = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($OutputPath + '.sha256.txt', "$sum  $([IO.Path]::GetFileName($OutputPath))`r`n", (New-Object Text.UTF8Encoding($false)))
Write-Output "Built $OutputPath"
Write-Output "Embedded files: $($names.Count + 1); ZIP prefix removed: $stripPrefix"
Write-Output "SHA-256: $sum"
Write-Output "Build artifacts: $BuildDirectory"
Write-Output 'No installation, user registry change, shortcut creation, or application launch was performed.'
