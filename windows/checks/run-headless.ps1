[CmdletBinding()]
param([ValidateSet('All', 'Backend', 'Drag')][string]$Suite = 'All')

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $projectRoot 'src'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 and its x64 compiler are required.' }
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$testDirectory = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ('ipmsg-dock-checks-' + [Guid]::NewGuid().ToString('N'))))
if (!$testDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    !([IO.Path]::GetFileName($testDirectory).StartsWith('ipmsg-dock-checks-', [StringComparison]::Ordinal))) {
    throw 'The generated checks directory is outside the intended temporary root.'
}
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null

function Compile-Check([string]$outputPath, [string[]]$sources) {
    & $compiler /nologo /langversion:5 /target:exe /platform:x64 /utf8output /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$outputPath" @sources
    if ($LASTEXITCODE -ne 0) { throw "Check compilation failed: $([IO.Path]::GetFileName($outputPath))" }
}
try {
    if ($Suite -eq 'All' -or $Suite -eq 'Backend') {
        $fakeRuntime = Join-Path $testDirectory 'runtime'
        $fakeKeys = Join-Path $testDirectory 'state\keys'
        New-Item -ItemType Directory -Path $fakeRuntime, $fakeKeys -Force | Out-Null
        # A fixture string, not an Android authorization key. Only fake-adb.exe reads it.
        [IO.File]::WriteAllText((Join-Path $fakeKeys 'adbkey'), 'fake-key')
        Compile-Check (Join-Path $fakeRuntime 'adb.exe') @((Join-Path $PSScriptRoot 'FakeAdb.cs'))
        $backend = Join-Path $testDirectory 'BackendChecks.exe'
        Compile-Check $backend @((Join-Path $PSScriptRoot 'RegressionChecks.cs'),
            (Join-Path $sourceRoot 'AdbBridge.cs'), (Join-Path $sourceRoot 'AdbProxy.cs'), (Join-Path $sourceRoot 'FileTransfer.cs'),
            (Join-Path $PSScriptRoot 'legacy\PreparedFiles.cs'), (Join-Path $sourceRoot 'VirtualFileDrag.cs'))
        & $backend
        if ($LASTEXITCODE -ne 0) { throw 'Backend regression checks failed.' }
    }
    if ($Suite -eq 'All' -or $Suite -eq 'Drag') {
        $drag = Join-Path $testDirectory 'DragChecks.exe'
        Compile-Check $drag @((Join-Path $PSScriptRoot 'DragRegression.cs'),
            (Join-Path $PSScriptRoot 'legacy\PreparedFiles.cs'), (Join-Path $sourceRoot 'VirtualFileDrag.cs'))
        & $drag
        if ($LASTEXITCODE -ne 0) { throw 'Local file-drop regression checks failed.' }
    }
    Write-Output 'Selected headless checks passed. No real ADB runtime, phone, application window or Explorer interaction was used.'
}
finally {
    # Remove only the verified, generated test directory. Fake ADB deliberately starts
    # a 12-second pipe-holder; it may briefly keep its executable open after the check.
    $removed = $false
    for ($attempt = 0; $attempt -lt 16; $attempt++) {
        try {
            if (Test-Path -LiteralPath $testDirectory) { Remove-Item -LiteralPath $testDirectory -Recurse -Force -ErrorAction Stop }
            $removed = $true
            break
        }
        catch { if ($attempt -lt 15) { Start-Sleep -Milliseconds 1000 } }
    }
    if (!$removed) { Write-Warning "Generated checks artifacts could not yet be removed: $testDirectory" }
}
