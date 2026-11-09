param([string]$BrokerExecutable)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ipmsg-bridge-runner-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
try {
    $output = Join-Path $fixture 'BridgeRegression.exe'
    $blockedBroker = Join-Path $fixture 'BlockedBroker.exe'
    $broker = $BrokerExecutable
    # Historical broker fixtures compile their own archived WinExe. The
    # production dock no longer contains this entry point or custom COM path.
    if (!$broker) {
        $broker = Join-Path $fixture 'LegacyBridge.exe'
        $legacySources = @((Join-Path $PSScriptRoot 'legacy\LegacyMain.cs'))
        foreach ($name in @('DockBridgeClient.cs','BridgeFileDrag.cs','BridgeDragBroker.cs','AdbBridge.cs','VirtualFileDrag.cs','FileTransfer.cs')) {
            $legacySources += if ($name -eq 'BridgeFileDrag.cs' -or $name -eq 'BridgeDragBroker.cs') { Join-Path $PSScriptRoot "legacy\$name" } else { Join-Path $project "src\$name" }
        }
        & $compiler /nologo /langversion:5 /target:winexe /platform:x64 /utf8output /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll "/out:$broker" @legacySources
        if ($LASTEXITCODE -ne 0) { throw 'Historical broker compilation failed.' }
    }
    $sources = @((Join-Path $PSScriptRoot 'BridgeRegression.cs'))
    foreach ($name in @('DockBridgeClient.cs','BridgeFileDrag.cs','BridgeDragBroker.cs','AdbBridge.cs','VirtualFileDrag.cs','FileTransfer.cs')) { $sources += if ($name -eq 'BridgeFileDrag.cs' -or $name -eq 'BridgeDragBroker.cs') { Join-Path $PSScriptRoot "legacy\$name" } else { Join-Path $project "src\$name" } }
    & $compiler /nologo /langversion:5 /target:exe /platform:x64 /utf8output /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll "/out:$output" @sources
    if ($LASTEXITCODE -ne 0) { throw 'Bridge check compilation failed.' }
    & $compiler /nologo /langversion:5 /target:winexe /platform:x64 "/out:$blockedBroker" (Join-Path $PSScriptRoot 'BridgeBlockedInputFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Blocked-input fixture compilation failed.' }
    & $output $broker $blockedBroker
    if ($LASTEXITCODE -ne 0) { throw 'Bridge component checks failed.' }
}
finally {
    $absolute = [IO.Path]::GetFullPath($fixture)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($absolute).StartsWith('ipmsg-bridge-runner-')) {
        try { Remove-Item -LiteralPath $absolute -Recurse -Force -ErrorAction Stop } catch { Write-Warning 'Generated check artifacts could not yet be removed.' }
    }
}
