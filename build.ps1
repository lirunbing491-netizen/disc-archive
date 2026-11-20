$ErrorActionPreference = 'Stop'
$version = '0.4.1-beta.1'
if ($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
    exit $LASTEXITCODE
}
$src = Join-Path $PSScriptRoot 'src'
$dist = Join-Path $PSScriptRoot 'dist'
$release = Join-Path $dist 'DiscArchive-Optical'
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
$actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
if ($actualVersion -ne $version) { throw "程序实际版本为 $actualVersion，目标为 $version。请同步 src/AssemblyInfo.cs 后重试，未更新发行压缩包。" }
Copy-Item -LiteralPath (Join-Path $src 'app.config') -Destination ($exe + '.config') -Force
Copy-Item -LiteralPath (Join-Path $src 'LabelOcr.ps1') -Destination (Join-Path $release 'LabelOcr.ps1') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '使用说明.txt') -Destination (Join-Path $release '使用说明.txt') -Force

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination (Join-Path $release 'LICENSE') -Force

# Explicit release allowlist: no CLI, GUI, installers, audit files, photos, or credentials.
$files = @('导盘助手.exe', '导盘助手.exe.config', 'LabelOcr.ps1', '使用说明.txt', 'LICENSE',
    'THIRD-PARTY-NOTICES.txt', 'native/x64/7z.dll', 'native/x86/7z.dll',
    'vendor/7zip/LICENSE-7zip.txt', 'vendor/7zip/COPYING-LGPL-2.1.txt',
    'vendor/7zip/unRarLicense.txt', 'vendor/7zip/downloads/7z2606-src.7z')
$dependencyFiles = @{
    'THIRD-PARTY-NOTICES.txt' = 'THIRD-PARTY-NOTICES.txt'
    'native/x64/7z.dll' = 'vendor/7zip/x64/7z.dll'
    'native/x86/7z.dll' = 'vendor/7zip/x86/7z.dll'
    'vendor/7zip/LICENSE-7zip.txt' = 'vendor/7zip/LICENSE-7zip.txt'
    'vendor/7zip/COPYING-LGPL-2.1.txt' = 'vendor/7zip/COPYING-LGPL-2.1.txt'
    'vendor/7zip/unRarLicense.txt' = 'vendor/7zip/unRarLicense.txt'
    'vendor/7zip/downloads/7z2606-src.7z' = 'vendor/7zip/downloads/7z2606-src.7z'
}
foreach ($relative in $dependencyFiles.Keys) {
    $inputFile = Join-Path $PSScriptRoot $dependencyFiles[$relative]
    if (!(Test-Path -LiteralPath $inputFile -PathType Leaf)) { throw "缺少依赖：$inputFile" }
    $outputFile = Join-Path $release $relative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $outputFile))
    Copy-Item -LiteralPath $inputFile -Destination $outputFile -Force
}
# The portable directory must also contain only the allowlist. Preserve unknown files and fail.
foreach ($existing in Get-ChildItem -LiteralPath $release -File -Recurse) {
    $relative = $existing.FullName.Substring($release.Length + 1).Replace('\','/')
    if ($files -notcontains $relative) { throw "发行目录存在白名单外文件，请保留并移到其他目录后重试：$relative" }
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $dist 'DiscArchive-Optical-Windows.zip'
$tempZip = Join-Path $dist ('DiscArchive-package-' + [Guid]::NewGuid().ToString('N') + '.zip')
$archive = $null
try {
    $archive = [IO.Compression.ZipFile]::Open($tempZip, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($name in $files) {
        $file = Join-Path $release $name
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "缺少发行文件：$name" }
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, ('DiscArchive-Optical/' + $name), [IO.Compression.CompressionLevel]::Optimal)
    }
    $archive.Dispose()
    $archive = $null
    Copy-Item -LiteralPath $tempZip -Destination $zipPath -Force
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if (Test-Path -LiteralPath $tempZip) { Remove-Item -LiteralPath $tempZip -Force }
}
Write-Host "Windows 测试版 v$version；发行文件数：$($files.Count)"
Write-Host "便携程序：$release"
Write-Host "发行压缩包：$zipPath"
