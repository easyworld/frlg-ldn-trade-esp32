[CmdletBinding()]
param(
    [ValidateSet('doctor', 'build', 'matrix', 'menuconfig', 'identify', 'flash', 'monitor', 'flash-monitor')]
    [string]$Action = 'doctor',
    [ValidateSet('esp32c6')][string]$Target = 'esp32c6',
    [ValidateSet('public', 'discovery', 'serial', 'send')][string]$Mode = 'serial',
    [ValidateSet('usb', 'uart')][string]$Console = 'uart',
    [ValidateSet('4MB', '8MB', '16MB')][string]$FlashSize = '16MB',
    [ValidateRange(1, 14)][int]$Channel = 1,
    [string]$Port,
    [ValidateRange(9600, 2000000)][int]$Baud = 460800,
    [string]$SdkPath = (Join-Path $env:USERPROFILE 'esp/v6.1/esp-idf')
)
$ErrorActionPreference = 'Stop'
if ($Action -notin @('doctor', 'matrix', 'identify') -and $Mode -eq 'serial' -and $Console -ne 'uart') {
    throw 'Serial bridge requires -Console uart.'
}
. (Join-Path $PSScriptRoot '../../tools/environment.ps1') -SdkPath $SdkPath
$project = Split-Path $PSScriptRoot -Parent

function Invoke-ProbePython {
    param([string[]]$Arguments)
    & $script:ProbePython @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Tool failed with exit code $LASTEXITCODE" }
}

if ($Action -eq 'doctor') {
    Invoke-ProbePython -Arguments @($script:ProbeIdf, '--version')
    & riscv32-esp-elf-gcc --version
    if ($LASTEXITCODE -ne 0) { throw 'Compiler is unavailable.' }
    & cmake --version
    if ($LASTEXITCODE -ne 0) { throw 'CMake is unavailable.' }
    & ninja --version
    if ($LASTEXITCODE -ne 0) { throw 'Ninja is unavailable.' }
    Invoke-ProbePython -Arguments @('-m', 'esptool', 'version')
    Invoke-ProbePython -Arguments @('-m', 'esp_idf_monitor', '--help')
    Invoke-ProbePython -Arguments @('-m', 'serial.tools.list_ports', '-v')
    Write-Host 'A listed serial adapter is not proof of an ESP32. Confirm the board before selecting a port.'
    return
}
if ($Action -in @('identify', 'flash', 'monitor', 'flash-monitor')) {
    if ($Port -notmatch '^COM[1-9][0-9]*$') { throw 'Specify the actual board port, for example -Port COM5.' }
}
if ($Action -eq 'identify') {
    Invoke-ProbePython -Arguments @('-m', 'esptool', '--port', $Port, 'chip-id')
    Invoke-ProbePython -Arguments @('-m', 'esptool', '--port', $Port, 'flash-id')
    return
}
if ($Action -eq 'matrix') {
    foreach ($variant in 'public', 'discovery', 'send') {
        & $PSCommandPath -Action build -Target $Target -Mode $variant -Console $Console -FlashSize $FlashSize -Channel $Channel -SdkPath $SdkPath
    }
    & $PSCommandPath -Action build -Target $Target -Mode serial -Console uart -FlashSize $FlashSize -Channel $Channel -SdkPath $SdkPath
    return
}

$shortTarget = $Target.Replace('esp32', '')
$modeCode = @{ public = 'pub'; discovery = 'adv'; serial = 'serial'; send = 'tx' }[$Mode]
$sizeCode = $FlashSize.Replace('MB', '')
$buildName = "build-$shortTarget-$modeCode-$Console$sizeCode-$Channel"
$build = Join-Path $project $buildName
New-Item -ItemType Directory -Path $build -Force | Out-Null
$defaults = @(
    "CONFIG_ESPTOOLPY_FLASHSIZE_$FlashSize=y",
    "CONFIG_LDN_PROBE_CHANNEL=$Channel"
)
if ($Console -eq 'usb') { $defaults += 'CONFIG_ESP_CONSOLE_USB_SERIAL_JTAG=y' }
else { $defaults += 'CONFIG_ESP_CONSOLE_UART_DEFAULT=y' }
if ($Mode -eq 'serial') {
    $defaults += @('CONFIG_LDN_PROBE_PRIVATE_JOIN=y', 'CONFIG_LDN_PROBE_EXPORT_ADVERTISEMENTS=y',
        'CONFIG_LDN_PROBE_CONTROL_PORT=y', 'CONFIG_LDN_PROBE_LEGACY_PHY=y',
        'CONFIG_LWIP_DHCPS_STATIC_ENTRIES=y')
    $defaults += 'CONFIG_LDN_PROBE_PRIVATE_RAW_TX=y'
}
if ($Mode -eq 'send') { $defaults += 'CONFIG_LDN_PROBE_SEND_TEST_ACTION=y' }
if ($Mode -eq 'discovery') { $defaults += 'CONFIG_LDN_PROBE_EXPORT_ADVERTISEMENTS=y' }
$defaultsPath = Join-Path $build 'probe.defaults'
$defaults | Set-Content -LiteralPath $defaultsPath -Encoding ascii
$defaultFiles = $defaultsPath.Replace('\', '/')
$configPath = Join-Path $build 'sdkconfig'
$idfArguments = @($script:ProbeIdf, '--ccache', '-C', $project, '-B', $build,
    "-DIDF_TARGET=$Target", "-DSDKCONFIG=$configPath", "-DSDKCONFIG_DEFAULTS=$defaultFiles")
if ($Action -eq 'menuconfig') {
    Invoke-ProbePython -Arguments ($idfArguments + @('menuconfig'))
    return
}
if ($Action -ne 'monitor') { Invoke-ProbePython -Arguments ($idfArguments + @('build')) }
if (Select-String -LiteralPath $configPath -Pattern '^CONFIG_LDN_PROBE_PRIVATE_RAW_TX=y$' -Quiet) {
    $objdump = (Get-Command riscv32-esp-elf-objdump).Source
    Invoke-ProbePython -Arguments @((Join-Path $PSScriptRoot 'audit_raw_link.py'),
        (Join-Path $build 'ldn_wifi_probe.elf'), '--objdump', $objdump)
}
if ($Action -eq 'build') {
    Write-Host "Firmware: $(Join-Path $build 'ldn_wifi_probe.bin')"
    return
}
if (-not (Test-Path (Join-Path $build 'ldn_wifi_probe.elf'))) { throw 'Build this profile before monitoring.' }
if ($Action -in @('flash', 'flash-monitor')) {
    Invoke-ProbePython -Arguments ($idfArguments + @('-p', $Port, '-b', "$Baud", 'flash'))
}
if ($Action -in @('monitor', 'flash-monitor')) {
    Invoke-ProbePython -Arguments ($idfArguments + @('-p', $Port, 'monitor'))
}
