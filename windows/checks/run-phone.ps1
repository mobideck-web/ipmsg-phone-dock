[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DeviceSerial,
    [Parameter(Mandatory = $true)][string]$StateDirectory,
    [Parameter(Mandatory = $true)][switch]$RunPhysicalChecks,
    [string]$RuntimeDirectory
)
$ErrorActionPreference = 'Stop'
if (!$RunPhysicalChecks) { throw 'Physical checks require the explicit -RunPhysicalChecks switch.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($RuntimeDirectory)) { $RuntimeDirectory = Join-Path $projectRoot 'runtime\scrcpy' }
$privateState = [IO.Path]::GetFullPath($StateDirectory)
$runtime = [IO.Path]::GetFullPath($RuntimeDirectory)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 and its x64 compiler are required.' }
if (!(Test-Path -LiteralPath (Join-Path $runtime 'adb.exe'))) { throw 'The selected Android tools runtime is missing.' }
New-Item -ItemType Directory -Path $privateState -Force | Out-Null
$executable = Join-Path $privateState 'NativePhoneRegression.exe'
$sourceRoot = Join-Path $projectRoot 'src'
$sources = @((Join-Path $PSScriptRoot 'NativePhoneRegression.cs'),
    (Join-Path $sourceRoot 'AdbBridge.cs'), (Join-Path $sourceRoot 'FileTransfer.cs'),
    (Join-Path $PSScriptRoot 'legacy\PreparedFiles.cs'), (Join-Path $sourceRoot 'VirtualFileDrag.cs'))
& $compiler /nologo /langversion:5 /target:exe /platform:x64 /utf8output /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$executable" @sources
if ($LASTEXITCODE -ne 0) { throw 'Physical-check compilation failed.' }
Write-Output 'Using only generated files in a new Downloads/IPMsg/dock-regression folder. No screen session or APK installation will start.'
& $executable $runtime $privateState $DeviceSerial
if ($LASTEXITCODE -ne 0) { throw 'Physical backend checks failed. Inspect the report in the selected private state directory.' }
