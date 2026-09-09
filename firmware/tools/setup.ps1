[CmdletBinding()]
param(
    [string]$SdkRoot = (Join-Path $env:USERPROFILE 'esp'),
    [switch]$UseGitHub
)
$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
$revision = 'fff9895c82d744c7237be8847347bdd1b07c6643'
$archiveHash = 'cdeea7db47b90064ef185b2a1f1b33d17bcb13469a9f8cc20e06c4c4cdb4cc16'
$sdk = Join-Path $SdkRoot 'esp-idf-v6.1'
$archive = Join-Path $SdkRoot 'esp-idf-v6.1.zip'
foreach ($command in 'git', 'python', 'curl.exe') {
    Get-Command $command -ErrorAction Stop | Out-Null
}
New-Item -ItemType Directory -Path $SdkRoot -Force | Out-Null
if (-not (Test-Path (Join-Path $sdk 'tools/idf.py'))) {
    if (Test-Path $sdk) { throw "Incomplete SDK at $sdk. Move it aside before retrying." }
    if (-not (Test-Path $archive)) {
        $base = 'https://dl.espressif.cn/github_assets'
        if ($UseGitHub) { $base = 'https://github.com' }
        & curl.exe --fail --location --retry 3 --connect-timeout 15 --output $archive "$base/espressif/esp-idf/releases/download/v6.1/esp-idf-v6.1.zip"
        if ($LASTEXITCODE -ne 0) { throw 'SDK download failed.' }
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) {
        throw "SDK archive checksum mismatch. Remove the incomplete archive and retry: $archive"
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $SdkRoot
    # Release archives preserve Unix modes and symlink contents, not Windows metadata.
    & git -C $sdk config core.filemode false
    & git -C $sdk config core.symlinks false
    & git -C $sdk submodule foreach --recursive 'git config core.filemode false'
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure SDK submodule file modes.' }
    & git -C $sdk submodule foreach --recursive 'git config core.symlinks false'
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure SDK submodule symlinks.' }
}
$actual = & git -C $sdk rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actual -ne $revision) { throw "Unexpected ESP-IDF revision: $actual" }
$submodules = & git -C $sdk submodule status --recursive
if ($LASTEXITCODE -ne 0 -or ($submodules | Where-Object { $_ -match '^[+-U]' })) {
    throw 'SDK submodules are missing or differ from the pinned release.'
}
if (-not $UseGitHub) { $env:IDF_GITHUB_ASSETS = 'dl.espressif.cn/github_assets' }
& python (Join-Path $sdk 'tools/idf_tools.py') --non-interactive --idf-path $sdk install --targets=esp32c6,esp32c3,esp32s3
if ($LASTEXITCODE -ne 0) { throw 'ESP-IDF tool installation failed.' }
& python (Join-Path $sdk 'tools/idf_tools.py') --non-interactive --idf-path $sdk install-python-env --features=core
if ($LASTEXITCODE -ne 0) { throw 'ESP-IDF Python environment installation failed.' }
Write-Host "Installed pinned ESP-IDF at $sdk"
Write-Host 'C6: .\firmware\esp32-c6\tools\probe.ps1 -Action doctor'
Write-Host 'C3: .\firmware\esp32-c3\tools\probe.ps1 -Action build'
Write-Host 'S3: .\firmware\esp32-s3\tools\probe.ps1 -Action build'
