param([string]$FixtureDirectory)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!$FixtureDirectory) { $FixtureDirectory = Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) ('work/dock-setup-checks-' + [Guid]::NewGuid().ToString('N')) }
$FixtureDirectory = [IO.Path]::GetFullPath($FixtureDirectory)
if (Test-Path -LiteralPath $FixtureDirectory) { throw 'FixtureDirectory must be new. Existing files are preserved.' }
$binaryDirectory = $FixtureDirectory + '-runner'
if (Test-Path -LiteralPath $binaryDirectory) { throw 'The check runner directory already exists.' }
New-Item -ItemType Directory -Path $binaryDirectory | Out-Null
$binary = Join-Path $binaryDirectory 'InstallerChecks.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /utf8output /codepage:65001 /warn:4 /langversion:5 /reference:System.dll /reference:System.Core.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$binary" (Join-Path $PSScriptRoot 'InstallerCore.cs') (Join-Path $PSScriptRoot 'WindowsIntegration.cs') (Join-Path $PSScriptRoot 'checks/InstallerChecks.cs') (Join-Path $PSScriptRoot 'checks/BuildInfo.cs')
if ($LASTEXITCODE -ne 0) { throw 'Installer checks did not compile.' }
& $binary $FixtureDirectory
if ($LASTEXITCODE -ne 0) { throw 'Installer safety checks failed.' }
Write-Output 'Checks used isolated workspace fixtures. No installed application, user registry, Start menu, Desktop, or private app state was changed.'
