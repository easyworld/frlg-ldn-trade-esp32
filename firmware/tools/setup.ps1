[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:USERPROFILE 'esp'),
    [switch]$UseGitHub
)
$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
if (-not (Get-Command eim -ErrorAction SilentlyContinue)) {
    throw 'eim was not found. Install it with: winget install Espressif.EIM-CLI'
}
if (-not $UseGitHub) {
    # dl.espressif.cn serves release assets only; git clones need an accelerator,
    # and the same rewrite is required for esp-idf submodules pointing at github.com.
    $env:GIT_CONFIG_COUNT = '1'
    $env:GIT_CONFIG_KEY_0 = 'url.https://gh-proxy.com/https://github.com/.insteadOf'
    $env:GIT_CONFIG_VALUE_0 = 'https://github.com/'
    $mirrors = @('-m', 'https://dl.espressif.cn/github_assets',
        '--idf-mirror', 'https://gh-proxy.com/https://github.com',
        '--pypi-mirror', 'https://pypi.tuna.tsinghua.edu.cn/simple')
} else {
    $mirrors = @()
}
& eim install -p $InstallRoot -i v6.1 -t esp32c6,esp32c3,esp32s3 -n true --do-not-track true --cleanup true @mirrors
if ($LASTEXITCODE -ne 0) { throw 'eim install failed.' }
Write-Host "Installed ESP-IDF v6.1 (pinned revision) under $InstallRoot; toolchains under $env:SystemDrive\Espressif\tools."
Write-Host 'C6: .\firmware\esp32-c6\tools\probe.ps1 -Action doctor'
Write-Host 'C3: .\firmware\esp32-c3\tools\probe.ps1 -Action build'
