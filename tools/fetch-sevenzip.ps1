param([string]$Destination = (Join-Path $PSScriptRoot '..\vendor\7zip'))
$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
$downloads = Join-Path $Destination 'downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
$assets = [ordered]@{
  '7zr.exe' = 'e80db2dad715a6c5adabe292d31ed54e11f4e9b99ca349827b50573b175891bc'
  '7z2606-x64.exe' = '9b697f65ecc2d9b0152f5ee96d72dde522a7827690349d9307935dacf40f42ec'
  '7z2606.exe' = '5e38f80c3752de43887e7a99ad10668c14bf64dcc929a636510b54616ea7dec7'
  '7z2606-src.7z' = 'e0364d39e8208557d939472c9a12d02ed1c12f45ca402e3b408bcaa63b36dd93'
}
# Digests copied from official ip7z/7zip 26.06 GitHub release asset metadata.
foreach ($name in $assets.Keys) {
  $path = Join-Path $downloads $name
  if (-not (Test-Path -LiteralPath $path)) {
    Invoke-WebRequest -UseBasicParsing "https://github.com/ip7z/7zip/releases/download/26.06/$name" -OutFile $path
  }
  if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $assets[$name]) {
    throw "SHA256 verification failed: $name. No executable will run."
  }
}
# Installer packages are archives here; the installer is NEVER executed.
$bootstrap = Join-Path $downloads '7zr.exe'
foreach ($pair in @(@('7z2606-x64.exe','x64'), @('7z2606.exe','x86'))) {
  $target = Join-Path $Destination $pair[1]
  if ((Test-Path -LiteralPath (Join-Path $target '7z.dll'))) {
    Write-Host "Keeping existing $target (user-replaceable library)."
    continue
  }
  & $bootstrap x (Join-Path $downloads $pair[0]) "-o$target" -y
  if ($LASTEXITCODE -ne 0) { throw "Extraction failed: $($pair[0])" }
}
& $bootstrap x (Join-Path $downloads '7z2606-src.7z') "-o$(Join-Path $Destination 'source')" -y
if ($LASTEXITCODE -ne 0) { throw 'Source extraction failed' }
# A source checkout can contain the DLL without the rest of the installer.
# Always obtain the binary license from the checksum-verified installer.
$licenseDir = Join-Path ([IO.Path]::GetTempPath()) ('DiscArchive-license-' + [Guid]::NewGuid().ToString('N'))
$licenseFile = Join-Path $licenseDir 'License.txt'
try {
  & $bootstrap e (Join-Path $downloads '7z2606-x64.exe') 'License.txt' "-o$licenseDir" -y
  if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $licenseFile -PathType Leaf)) { throw 'Could not extract the official binary license.' }
  Copy-Item -LiteralPath $licenseFile -Destination (Join-Path $Destination 'LICENSE-7zip.txt') -Force
} finally {
  if (Test-Path -LiteralPath $licenseFile -PathType Leaf) { Remove-Item -LiteralPath $licenseFile }
  if ((Test-Path -LiteralPath $licenseDir -PathType Container) -and [IO.Directory]::GetFileSystemEntries($licenseDir).Length -eq 0) { [IO.Directory]::Delete($licenseDir) }
}
Copy-Item -LiteralPath (Join-Path $Destination 'source\DOC\copying.txt') -Destination (Join-Path $Destination 'COPYING-LGPL-2.1.txt')
Copy-Item -LiteralPath (Join-Path $Destination 'source\DOC\unRarLicense.txt') -Destination (Join-Path $Destination 'unRarLicense.txt')
Write-Host "Verified the 7-Zip 26.06 download archives and extracted their notices. Existing DLLs were retained in $Destination."
