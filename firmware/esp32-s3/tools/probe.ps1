[CmdletBinding()]
param(
    [ValidateSet('build', 'flash', 'identify')][string]$Action = 'build',
    [string]$Port,
    [string]$SdkPath = (Join-Path $env:USERPROFILE 'esp/esp-idf-v6.1')
)
$ErrorActionPreference = 'Stop'
if ($Action -ne 'build' -and $Port -notmatch '^COM[1-9][0-9]*$') { throw 'Specify the S3 port with -Port COM<number>.' }
. (Join-Path $PSScriptRoot '../../tools/environment.ps1') -SdkPath $SdkPath
function Invoke-S3Python {
    param([string[]]$Arguments)
    & $script:ProbePython @Arguments
    if ($LASTEXITCODE -ne 0) { throw "S3 tool failed with exit code $LASTEXITCODE" }
}
$project = Split-Path $PSScriptRoot -Parent
$build = Join-Path $project 'build'
if ($Action -eq 'identify') {
    Invoke-S3Python @('-m', 'esptool', '--chip', 'esp32s3', '--port', $Port, 'flash-id')
    return
}
New-Item -ItemType Directory -Path $build -Force | Out-Null
$idfArguments = @($script:ProbeIdf, '--ccache', '-C', $project, '-B', $build,
    '-DIDF_TARGET=esp32s3', "-DSDKCONFIG=$(Join-Path $build 'sdkconfig')",
    "-DSDKCONFIG_DEFAULTS=$(Join-Path $project 'sdkconfig.defaults')")
Invoke-S3Python ($idfArguments + @('build'))
if ($Action -eq 'flash') {
    # esptool checks the actual chip against the S3 image before writing.
    Invoke-S3Python ($idfArguments + @('-p', $Port, '-b', '460800', 'flash'))
}
Write-Host "S3 firmware: $(Join-Path $build 'ldn_s3_bridge.bin')"
