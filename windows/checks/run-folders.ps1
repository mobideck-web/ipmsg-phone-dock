$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ipmsg-folder-runner-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    $output = Join-Path $fixture 'FolderSnapshotChecks.exe'
    $sources = @((Join-Path $PSScriptRoot 'FolderSnapshotChecks.cs'))
    foreach ($name in @('FolderSnapshot.cs','TemporaryBridgeCache.cs','DockBridgeClient.cs','VirtualFileDrag.cs','FileTransfer.cs','AdbBridge.cs')) { $sources += Join-Path $project "src\$name" }
    & $compiler /nologo /target:exe /platform:x64 /langversion:5 /utf8output /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$output" @sources
    if ($LASTEXITCODE -ne 0) { throw 'Folder check compilation failed.' }
    & $output
    if ($LASTEXITCODE -ne 0) { throw 'Folder safety checks failed.' }
}
finally {
    $absolute = [IO.Path]::GetFullPath($fixture)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($absolute).StartsWith('ipmsg-folder-runner-')) {
        Get-ChildItem -LiteralPath $absolute -File | Remove-Item -Force
        Remove-Item -LiteralPath $absolute
    }
}
