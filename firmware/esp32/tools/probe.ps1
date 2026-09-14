[CmdletBinding()]
param(
    [ValidateSet('build', 'flash', 'identify')][string]$Action = 'build',
    [string]$Port,
    [string]$SdkPath = (Join-Path $env:USERPROFILE 'esp/esp-idf-v6.1')
)
$ErrorActionPreference = 'Stop'
if ($Action -ne 'build' -and $Port -notmatch '^COM[1-9][0-9]*$') {
    throw 'Specify the classic ESP32 port with -Port COM<number>.'
}
. (Join-Path $PSScriptRoot '../../tools/environment.ps1') -SdkPath $SdkPath
function Invoke-Esp32Python {
    param([string[]]$Arguments)
    & $script:ProbePython @Arguments
    if ($LASTEXITCODE -ne 0) { throw "ESP32 tool failed with exit code $LASTEXITCODE" }
}
$project = Split-Path $PSScriptRoot -Parent
$build = Join-Path $project 'build'
if ($Action -eq 'identify') {
    Invoke-Esp32Python @('-m', 'esptool', '--chip', 'esp32', '--port', $Port, 'flash-id')
    return
}
New-Item -ItemType Directory -Path $build -Force | Out-Null
$idfArguments = @($script:ProbeIdf, '--ccache', '-C', $project, '-B', $build,
    '-DIDF_TARGET=esp32', "-DSDKCONFIG=$(Join-Path $build 'sdkconfig')",
    "-DSDKCONFIG_DEFAULTS=$(Join-Path $project 'sdkconfig.defaults')")
Invoke-Esp32Python ($idfArguments + @('build'))
if ($Action -eq 'flash') {
    Invoke-Esp32Python ($idfArguments + @('-p', $Port, '-b', '460800', 'flash'))
}
Write-Host "Classic ESP32 firmware: $(Join-Path $build 'ldn_esp32_bridge.bin')"
