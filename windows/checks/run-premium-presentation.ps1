param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('ipmsg-premium-fixture-' + [Guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$fixture = (Resolve-Path -LiteralPath $OutputDirectory).Path
$sources = @(Get-ChildItem -LiteralPath (Join-Path $project 'src') -Filter '*.cs' | ForEach-Object FullName)
$runner = Join-Path $fixture 'PremiumChecks.exe'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /target:exe /main:PremiumPresentationChecks /platform:x64 /langversion:5 /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/resource:$project/assets/lucide.ttf,IPMsg.Dock.Lucide.ttf" "/reference:$project/QRCoder.dll" "/out:$runner" (Join-Path $PSScriptRoot 'PremiumPresentationChecks.cs') $sources
if ($LASTEXITCODE -ne 0) { throw 'Presentation fixture compilation failed.' }
Copy-Item -LiteralPath (Join-Path $project 'QRCoder.dll') -Destination $fixture -Force
& $runner $fixture
if ($LASTEXITCODE -ne 0) { throw 'Presentation fixtures failed.' }
