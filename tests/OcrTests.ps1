$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
    exit $LASTEXITCODE
}

# All generated images, executables and helper modifications stay in a new temp folder.
$testDir = Join-Path $env:TEMP ('DiscArchive OCR ' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $testDir)
$sourceDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'src'
Copy-Item -LiteralPath (Join-Path $sourceDir 'LabelOcr.ps1') -Destination $testDir
$languages=@()
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    [void][Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
    $languages = @([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object { $_.LanguageTag })
} catch { Write-Output ('SKIP Windows OCR availability: ' + $_.Exception.Message) }
$hasEnglish = @($languages | Where-Object { $_ -like 'en*' }).Count -gt 0
$hasChinese = @($languages | Where-Object { $_ -like 'zh-Hans*' -or $_ -eq 'zh-CN' }).Count -gt 0
if (-not $hasEnglish) { Write-Output 'SKIP English image recognition assertion: English recognizer unavailable.' }
if (-not $hasChinese) { Write-Output 'SKIP simplified Chinese recognition assertion: simplified Chinese recognizer unavailable.' }
Write-Output ('Installed OCR languages: ' + ($languages -join ', '))
Add-Type -AssemblyName System.Drawing
$png = Join-Path $testDir (([char]0x4e2d).ToString() + ' image.png')
$jpg = [IO.Path]::ChangeExtension($png, '.jpg')
$chinese = Join-Path $testDir 'chinese.png'
$image = New-Object Drawing.Bitmap(5000,3000)
$graphics = [Drawing.Graphics]::FromImage($image)
$font = New-Object Drawing.Font('Arial',150,[Drawing.FontStyle]::Bold)
$chineseFont = New-Object Drawing.Font('Microsoft YaHei',150,[Drawing.FontStyle]::Bold)
try {
    $graphics.Clear([Drawing.Color]::White)
    $graphics.DrawString('DISC ARCHIVE 2024', $font, [Drawing.Brushes]::Black, 150, 300)
    $graphics.DrawString('ALBUM TITLE', $font, [Drawing.Brushes]::Black, 150, 700)
    $image.Save($png, [Drawing.Imaging.ImageFormat]::Png)
    $image.Save($jpg, [Drawing.Imaging.ImageFormat]::Jpeg)
    $graphics.Clear([Drawing.Color]::White)
    $label = -join ([char[]]@(0x5149,0x76d8,0x6863,0x6848,0x6d4b,0x8bd5,0x6807,0x9898))
    $graphics.DrawString($label, $chineseFont, [Drawing.Brushes]::Black, 150, 300)
    $image.Save($chinese, [Drawing.Imaging.ImageFormat]::Png)
}
finally { $graphics.Dispose(); $image.Dispose(); $font.Dispose(); $chineseFont.Dispose() }
$corrupt = Join-Path $testDir 'corrupt.jpg'
[IO.File]::WriteAllText($corrupt, 'invalid image')
$harness = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using DiscArchive;
class OcrTests {
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static void Main(string[] args) {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args[0] == "timeout") {
            var timer = Stopwatch.StartNew();
            Check(LabelOcr.Read(args[1]) == "", "timeout returns empty");
            Check(timer.ElapsedMilliseconds >= 19000 && timer.ElapsedMilliseconds < 23000, "bounded timeout: " + timer.ElapsedMilliseconds + "ms");
            return;
        }
        foreach (string path in new string[] { args[0], args[1] }) {
            string result = LabelOcr.Read(path);
            Console.WriteLine("OCR " + Path.GetFileName(path) + ": " + result);
            if (args[4] == "True") Check(result.Contains("DISC ARCHIVE 2024") && result.Contains("ALBUM TITLE"), "large image and Unicode path");
        }
        string chinese = LabelOcr.Read(args[2]);
        Console.WriteLine("Chinese OCR: " + chinese);
        if (args[5] == "True") Check(chinese.Contains("\u5149\u76d8\u6863\u6848\u6d4b\u8bd5\u6807\u9898"), "Chinese OCR and spacing");
        Check(LabelOcr.Read(args[3]) == "", "corrupt image returns empty");
        Check(LabelOcr.Read(Path.Combine(Path.GetDirectoryName(args[0]), "missing.png")) == "", "missing image returns empty");
        Check(LabelOcr.Read(null) == "", "null returns empty");
    }
}
'@
$harnessPath = Join-Path $testDir 'OcrTests.cs'
[IO.File]::WriteAllText($harnessPath, $harness, (New-Object Text.UTF8Encoding($true)))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$exe = Join-Path $testDir 'OcrTests.exe'
& $compiler /nologo /langversion:5 /codepage:65001 /target:exe "/out:$exe" (Join-Path $sourceDir 'LabelOcr.cs') $harnessPath
if ($LASTEXITCODE -ne 0) { throw 'C# 5 compilation failed.' }
& $exe $png $jpg $chinese $corrupt $hasEnglish.ToString() $hasChinese.ToString()
if ($LASTEXITCODE -ne 0) { throw 'OCR tests failed.' }
# A sleeping local stub verifies that the C# caller actually kills stalled workers.
[IO.File]::WriteAllText((Join-Path $testDir 'LabelOcr.ps1'), 'param([string]$PhotoPath) Start-Sleep -Seconds 60')
& $exe 'timeout' $png
if ($LASTEXITCODE -ne 0) { throw 'Timeout test failed.' }
Write-Output ('Test evidence retained at: ' + $testDir)
