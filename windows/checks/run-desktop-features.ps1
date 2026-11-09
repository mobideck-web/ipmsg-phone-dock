$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('IPMsgDock-desktop-checks-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$runner = Join-Path $outputDirectory 'DesktopFeatureChecks.exe'
$sources = @('DesktopIntegration.cs','PhoneScreenshot.cs','AdbBridge.cs','NativeGlass.cs','GlassUi.cs','LucideIcons.cs') | ForEach-Object { Join-Path $projectRoot ('src/' + $_) }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /langversion:5 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "/out:$runner" (Join-Path $PSScriptRoot 'DesktopFeatureChecks.cs') $sources
if ($LASTEXITCODE -ne 0) { throw 'Desktop feature fixture compilation failed.' }
& $runner
if ($LASTEXITCODE -ne 0) { throw 'Desktop feature fixture checks failed.' }
