$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ipmsg-preparation-runner-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    $output = Join-Path $fixture 'BridgePreparationChecks.exe'
    $sources = @((Join-Path $PSScriptRoot 'BridgePreparationChecks.cs'))
    foreach ($name in @('DockBridgeClient.cs', 'BridgePreparation.cs', 'FolderSnapshot.cs', 'VirtualFileDrag.cs', 'FileTransfer.cs', 'AdbBridge.cs')) {
        $sources += Join-Path $project "src\$name"
    }
    & $compiler /nologo /langversion:5 /target:exe /platform:x64 /utf8output /codepage:65001 /warn:4 /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$output" @sources
    if ($LASTEXITCODE -ne 0) { throw 'Preparation check compilation failed.' }
    & $output
    if ($LASTEXITCODE -ne 0) { throw 'Preparation checks failed.' }
}
finally {
    $absolute = [IO.Path]::GetFullPath($fixture)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($absolute).StartsWith('ipmsg-preparation-runner-')) {
        try { Remove-Item -LiteralPath $absolute -Recurse -Force -ErrorAction Stop } catch { Write-Warning 'Generated check runner could not yet be removed.' }
    }
}
