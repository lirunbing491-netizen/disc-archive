$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'src'
$dist = Join-Path $PSScriptRoot 'dist'
$release = Join-Path $dist 'DiscArchive'
[void][IO.Directory]::CreateDirectory($release)
$exe = Join-Path $release '导盘助手.exe'
$compiler = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (!(Test-Path -LiteralPath $compiler)) { throw '找不到 .NET Framework C# 编译器。请在 Windows 上安装 .NET Framework 4.7.2 或更高版本。' }
$inputs = @((Get-ChildItem -LiteralPath $src -Filter '*.cs' | Sort-Object Name).FullName)
if ($inputs.Count -eq 0) { throw 'src 目录中没有 C# 源文件。' }
& $compiler /nologo /langversion:5 /codepage:65001 /optimize+ /target:winexe /platform:anycpu "/win32manifest:$src\app.manifest" "/out:$exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll /r:System.Security.dll $inputs
if ($LASTEXITCODE -ne 0) { throw '编译失败，未更新发行压缩包。' }
Copy-Item -LiteralPath (Join-Path $src 'app.config') -Destination ($exe + '.config') -Force
Copy-Item -LiteralPath (Join-Path $src 'LabelOcr.ps1') -Destination (Join-Path $release 'LabelOcr.ps1') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '使用说明.txt') -Destination (Join-Path $release '使用说明.txt') -Force

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination (Join-Path $release 'LICENSE') -Force

# 只打包这五个文件；即使 dist 中有其他文件，也不会进入发行包。
Add-Type -AssemblyName System.IO.Compression.FileSystem
$files = @('导盘助手.exe', '导盘助手.exe.config', 'LabelOcr.ps1', '使用说明.txt', 'LICENSE')
$zipPath = Join-Path $dist 'DiscArchive-Windows.zip'
$tempZip = Join-Path $dist ('DiscArchive-package-' + [Guid]::NewGuid().ToString('N') + '.zip')
$archive = $null
try {
    $archive = [IO.Compression.ZipFile]::Open($tempZip, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($name in $files) {
        $file = Join-Path $release $name
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "缺少发行文件：$name" }
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, ('DiscArchive/' + $name), [IO.Compression.CompressionLevel]::Optimal)
    }
    $archive.Dispose()
    $archive = $null
    Copy-Item -LiteralPath $tempZip -Destination $zipPath -Force
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if (Test-Path -LiteralPath $tempZip) { Remove-Item -LiteralPath $tempZip -Force }
}
Write-Host "便携程序：$release"
Write-Host "发行压缩包：$zipPath"