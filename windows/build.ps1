param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Install .NET Framework 4.8 developer/runtime tools on Windows x64.' }
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
if (!$OutputPath) { $OutputPath = Join-Path $projectRoot 'IPMsgDock.exe' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output /codepage:65001 /warn:4 "/resource:$projectRoot/assets/lucide.ttf,IPMsg.Dock.Lucide.ttf" /langversion:5 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/reference:$projectRoot/QRCoder.dll" "/win32manifest:$projectRoot/app.manifest" "/win32icon:$projectRoot/assets/ipmsg.ico" "/out:$OutputPath" $sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output 'Compiled IPMsgDock.exe. No tests or device sessions were run.'
