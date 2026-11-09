$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$outputDirectory = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ('IPMsgDock-connection-checks-' + [Guid]::NewGuid().ToString('N'))))
if (!$outputDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    !([IO.Path]::GetFileName($outputDirectory).StartsWith('IPMsgDock-connection-checks-', [StringComparison]::Ordinal))) {
    throw 'The generated checks directory is outside the intended temporary root.'
}
New-Item -ItemType Directory -Path (Join-Path $outputDirectory 'runtime') | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
try {
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /langversion:5 /reference:System.dll /reference:System.Core.dll "/out:$outputDirectory/runtime/adb.exe" (Join-Path $PSScriptRoot 'ConnectionFakeAdb.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Connection process fixture compilation failed.' }
    $runner = Join-Path $outputDirectory 'ConnectionRecoveryChecks.exe'
    $sources = @('AdbBridge.cs', 'Connections.cs') | ForEach-Object { Join-Path $projectRoot ('src/' + $_) }
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /langversion:5 /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll "/out:$runner" (Join-Path $PSScriptRoot 'ConnectionRecoveryChecks.cs') $sources
    if ($LASTEXITCODE -ne 0) { throw 'Connection recovery fixture compilation failed.' }
    & $runner
    if ($LASTEXITCODE -ne 0) { throw 'Connection recovery checks failed.' }
}
finally {
    # This is the same absolute, generated directory checked against the temporary root above.
    if (Test-Path -LiteralPath $outputDirectory) { Remove-Item -LiteralPath $outputDirectory -Recurse -Force }
}
