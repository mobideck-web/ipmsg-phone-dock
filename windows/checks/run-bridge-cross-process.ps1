$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ipmsg-bridge-cross-process-runner-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
try {
    $output = Join-Path $fixture 'CrossProcessBridgeChecks.exe'
    $sources = @((Join-Path $PSScriptRoot 'CrossProcessBridgeChecks.cs'))
    foreach ($name in @('DockBridgeClient.cs','BridgeFileDrag.cs','BridgeDragBroker.cs','AdbBridge.cs','VirtualFileDrag.cs','FileTransfer.cs')) {
        $sources += if ($name -eq 'BridgeFileDrag.cs' -or $name -eq 'BridgeDragBroker.cs') { Join-Path $PSScriptRoot "legacy\$name" } else { Join-Path $project "src\$name" }
    }
    & $compiler /nologo /langversion:5 /target:exe /platform:x64 /utf8output /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll "/out:$output" @sources
    if ($LASTEXITCODE -ne 0) { throw 'Cross-process bridge check compilation failed.' }
    # A private child uses native COM vtables over MSHCTX_LOCAL proxies. The
    # source pumps COM on its STA while the receiver waits on pending bytes.
    # No phone, mouse input, visible window, Explorer or user files are used.
    & $output
    if ($LASTEXITCODE -ne 0) { throw 'Cross-process bridge checks failed.' }
}
finally {
    $absolute = [IO.Path]::GetFullPath($fixture)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($absolute).StartsWith('ipmsg-bridge-cross-process-runner-')) {
        try { Remove-Item -LiteralPath $absolute -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning 'Generated cross-process check artifacts could not yet be removed.' }
    }
}
