param([string]$CacheRoot='',[string]$OutputJar='')
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if(-not $CacheRoot) {$CacheRoot=Join-Path $taskRoot 'work/gradle-v074/caches/modules-2/files-2.1'}
if(-not $OutputJar) {$OutputJar=Join-Path $taskRoot 'work/android-folder-jvm.jar'}
function Jar([string]$relative) {
    $found=@(Get-ChildItem -LiteralPath (Join-Path $CacheRoot $relative) -Recurse -Filter '*.jar')
    if($found.Count -ne 1) {throw "Expected one cached JAR: $relative"}
    return $found[0].FullName
}
$compiler=@(
    (Jar 'org.jetbrains.kotlin/kotlin-compiler-embeddable/2.1.20'),
    (Jar 'org.jetbrains.kotlin/kotlin-stdlib/2.1.20'),
    (Jar 'org.jetbrains.kotlin/kotlin-script-runtime/2.1.20'),
    (Jar 'org.jetbrains.kotlin/kotlin-reflect/1.6.10'),
    (Jar 'org.jetbrains.kotlin/kotlin-daemon-embeddable/2.1.20'),
    (Jar 'org.jetbrains.intellij.deps/trove4j/1.0.20200330'),
    (Jar 'org.jetbrains.kotlinx/kotlinx-coroutines-core-jvm/1.8.0'),
    (Jar 'org.jetbrains/annotations/23.0.0')
) -join ';'
$runtime=@((Jar 'org.jetbrains.kotlin/kotlin-stdlib/2.1.20'),(Jar 'junit/junit/4.13.2'),(Jar 'org.hamcrest/hamcrest-core/1.3'),(Jar 'org.jetbrains/annotations/23.0.0')) -join ';'
$app=Join-Path $PSScriptRoot '../app/src'
$source=@('main/java/app/lantern/lan/DockFolderArchive.kt','main/java/app/lantern/lan/DockWire.kt',
    'test/java/app/lantern/lan/DockFolderArchiveTest.kt','test/java/app/lantern/lan/DockWireTest.kt') | ForEach-Object {Join-Path $app $_}
& java -Xmx512m -cp $compiler org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect -jvm-target 17 -cp $runtime -d $OutputJar @source
if($LASTEXITCODE -ne 0) {throw 'Offline Kotlin compilation failed.'}
& java -Xmx512m -cp "$OutputJar;$runtime" org.junit.runner.JUnitCore app.lantern.lan.DockFolderArchiveTest app.lantern.lan.DockWireTest
if($LASTEXITCODE -ne 0) {throw 'Dock folder/wire checks failed.'}
