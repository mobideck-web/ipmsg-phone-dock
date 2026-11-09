param([Parameter(Mandatory=$true)][string]$DecoderDll)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$decoder = (Resolve-Path -LiteralPath $DecoderDll).Path
$temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$fixture = Join-Path $temporary ('ipmsg-quick-checks-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    Copy-Item -LiteralPath $decoder -Destination (Join-Path $fixture 'zxing.dll')
    Copy-Item -LiteralPath (Join-Path $project 'QRCoder.dll') -Destination $fixture
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $project 'src') -Filter '*.cs' | ForEach-Object FullName)
    $runner = Join-Path $fixture 'QuickChecks.exe'
    & (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /target:exe /main:QuickControlChecks /platform:x64 /langversion:5 /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/resource:$project/assets/lucide.ttf,IPMsg.Dock.Lucide.ttf" "/reference:$project/QRCoder.dll" "/reference:$decoder" "/out:$runner" (Join-Path $PSScriptRoot 'QuickControlChecks.cs') $sources
    if ($LASTEXITCODE -ne 0) { throw 'Quick-control check compilation failed.' }
    & $runner
    if ($LASTEXITCODE -ne 0) { throw 'Quick-control fixtures failed.' }
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ($resolved.StartsWith($temporary,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('ipmsg-quick-checks-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
