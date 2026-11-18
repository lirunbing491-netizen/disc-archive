$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
    exit $LASTEXITCODE
}
$source=Join-Path (Split-Path $PSScriptRoot -Parent) 'src'
$testDir=Join-Path $env:TEMP ('DiscArchive Vision '+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testDir)
$script=Join-Path $source 'LabelOcr.ps1'
$helper=[IO.File]::ReadAllText((Join-Path $source 'DiscVision.cs')).Trim()
if(-not [IO.File]::ReadAllText($script).Contains($helper)){throw 'Embedded crop source differs from tested C# source.'}
Copy-Item -LiteralPath $script -Destination $testDir
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not (Test-Path -LiteralPath $compiler)) { $compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$exe=Join-Path $testDir 'VisionTests.exe'
& $compiler /nologo /langversion:5 /codepage:65001 /target:exe /r:System.Drawing.dll "/out:$exe" (Join-Path $source 'LabelOcr.cs') (Join-Path $source 'DiscVision.cs') (Join-Path $PSScriptRoot 'VisionTests.cs')
if($LASTEXITCODE -ne 0){throw 'Compilation failed'}
$langs=@()
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    [void][Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
    $langs=@([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object {$_.LanguageTag})
} catch { Write-Output ('SKIP Windows OCR availability: ' + $_.Exception.Message) }
Write-Output ('Installed languages: '+($langs -join ', '))
$mode='no-language-assertion'
if(@($langs | Where-Object {$_ -like 'en*'}).Count -gt 0){$mode='english'}else{Write-Output 'SKIP printed-disc English OCR assertion: English recognizer unavailable.'}
& $exe $mode
if($LASTEXITCODE -ne 0){throw 'Vision tests failed'}
[IO.File]::WriteAllText((Join-Path $testDir 'LabelOcr.ps1'),'param([string]$PhotoPath) Start-Sleep -Seconds 60')
& $exe timeout (Join-Path $testDir 'synthetic-disc.png')
if($LASTEXITCODE -ne 0){throw 'Deadline test failed'}
Write-Output ('Evidence: '+$testDir)
