$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('IPMsgDock-button-checks-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$runner = Join-Path $outputDirectory 'ButtonRepaintChecks.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /langversion:5 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "/resource:$projectRoot/assets/lucide.ttf,IPMsg.Dock.Lucide.ttf" "/out:$runner" (Join-Path $PSScriptRoot 'ButtonRepaintChecks.cs') (Join-Path $projectRoot 'src/GlassUi.cs') (Join-Path $projectRoot 'src/NativeGlass.cs') (Join-Path $projectRoot 'src/LucideIcons.cs')
if ($LASTEXITCODE -ne 0) { throw 'Button repaint check compilation failed.' }
& $runner
if ($LASTEXITCODE -ne 0) { throw 'Button repaint checks failed.' }
Write-Output 'Component-only checks complete. No app or phone session was launched.'
