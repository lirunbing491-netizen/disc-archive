# Run with Windows PowerShell 5.1. All fixtures are generated under a fresh temp directory.
[CmdletBinding()]
param([switch]$EnumerateHardware)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $forward = @('-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath)
    if ($EnumerateHardware) { $forward += '-EnumerateHardware' }
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @forward
    exit $LASTEXITCODE
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework C# compiler not found.' }
$source = Join-Path $PSScriptRoot 'src'
$inputs = @(Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | ForEach-Object { $_.FullName })
if ($inputs.Count -eq 0) { throw 'Public src/*.cs is missing.' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('DiscArchive-PublicTests-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
Write-Output ('Generated test artifacts: ' + $work)
Write-Output 'No physical media or camera capture is used. UI construction may enumerate cameras read-only. No external network requests. Model requests use loopback only. RawOptical --drive is never passed by this runner.'
$count = 0
foreach ($name in @('CameraFileDate','Copy','Catalog','Plan','Rescue','Optical','Camera','CompletionNotice','VisionSettings','Endpoint','Workflow','MultiDrive','Identity','Alias','Choice','ContinuationWorkflow','PauseRestart','ArchiveReader','ArchiveTree','HfsGuard','Layout','RawOptical','SourcePipeline','OpticalMount','TrackInfo','MountCleanup','CloseOutcome')) {
    $dir = Join-Path $work $name
    [void][IO.Directory]::CreateDirectory($dir)
    $exe = Join-Path $dir ($name + 'Tests.exe')
    $entry = $name + 'Tests'
    if ($name -in @('Workflow','MultiDrive')) { $entry = 'DiscArchive.' + $name + 'Tests' }
    $testSource = Join-Path $PSScriptRoot ('tests\' + $name + 'Tests.cs')
    & $compiler /nologo /langversion:5 /codepage:65001 /target:exe /platform:anycpu "/win32manifest:$source\app.manifest" "/main:$entry" "/out:$exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll /r:System.Security.dll $inputs $testSource
    if ($LASTEXITCODE -ne 0) { throw ($name + ' test compilation failed.') }
    Copy-Item -LiteralPath (Join-Path $source 'app.config') -Destination ($exe + '.config')
    Copy-Item -LiteralPath (Join-Path $source 'LabelOcr.ps1') -Destination $dir
    # Copy both architectures into each isolated runner's actual default DLL layout.
    foreach ($architecture in @('x64','x86')) {
        $dllSource = Join-Path $PSScriptRoot ('vendor\7zip\' + $architecture + '\7z.dll')
        if (-not (Test-Path -LiteralPath $dllSource -PathType Leaf)) { throw ('Missing official DLL: ' + $dllSource) }
        $nativeDir = Join-Path $dir ('native\' + $architecture)
        [void][IO.Directory]::CreateDirectory($nativeDir)
        Copy-Item -LiteralPath $dllSource -Destination (Join-Path $nativeDir '7z.dll')
    }
    $testArgs = @()
    if ($name -eq 'ArchiveReader') {
        $processArchitecture = if ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' }
        $testArgs += (Join-Path $dir ('native\' + $processArchitecture + '\7z.dll'))
    }
    if ($EnumerateHardware -and $name -in @('Optical','Camera')) { $testArgs += '--enumerate-read-only' }
    if ($name -eq 'Workflow') {
        # Exercise the real OCR worker process boundary without depending on installed language packs.
        # Real image recognition is tested separately below using generated images.
        $stub = 'param([string]$PhotoPath) [Console]::OutputEncoding=New-Object Text.UTF8Encoding($false); [Console]::Write("ARCHIVE 2024")'
        [IO.File]::WriteAllText((Join-Path $dir 'LabelOcr.ps1'), $stub, (New-Object Text.UTF8Encoding($true)))
    }
    Write-Output ('RUN ' + $entry + ' [' + $exe + ']')
    & $exe @testArgs
    if ($LASTEXITCODE -ne 0) { throw ($name + ' tests failed. Generated artifacts: ' + $dir) }
    $count++
}
foreach ($script in @('VisionTests.ps1','OcrTests.ps1')) {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot ('tests\' + $script))
    if ($LASTEXITCODE -ne 0) { throw ($script + ' failed.') }
    $count++
}
Write-Output ('PASS ' + $count + ' test suites. OCR language-dependent checks explicitly report SKIP when unavailable.')
Write-Output ('Generated artifacts retained for inspection: ' + $work)
