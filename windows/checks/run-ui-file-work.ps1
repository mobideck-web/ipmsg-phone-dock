$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ipmsg-ui-file-work-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $project 'src') -Filter '*.cs' | ForEach-Object FullName)
$output = Join-Path $fixture 'FileWorkChecks.exe'
try {
    & (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /target:exe /main:UiFileWorkChecks /platform:x64 /langversion:5 /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/reference:$project/QRCoder.dll" "/resource:$project/assets/lucide.ttf,IPMsg.Dock.Lucide.ttf" "/out:$output" (Join-Path $PSScriptRoot 'UiFileWorkChecks.cs') $sources
    if ($LASTEXITCODE -ne 0) { throw 'File-work checks did not compile.' }
    Copy-Item -LiteralPath (Join-Path $project 'QRCoder.dll') -Destination $fixture
    & $output
    if ($LASTEXITCODE -ne 0) { throw 'File-work cancellation checks failed.' }
}
finally {
    foreach ($name in @('FileWorkChecks.exe', 'QRCoder.dll')) {
        $path = Join-Path $fixture $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    if ((Get-ChildItem -LiteralPath $fixture -Force).Count -eq 0) { Remove-Item -LiteralPath $fixture }
}
