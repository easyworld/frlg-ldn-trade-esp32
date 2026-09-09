param(
    [string]$SdkPath = (Join-Path $env:USERPROFILE 'esp/v6.1/esp-idf'),
    [string]$EimRegistry = "$env:SystemDrive\Espressif\tools\eim_idf.json"
)
$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
$expectedRevision = 'fff9895c82d744c7237be8847347bdd1b07c6643'
if (-not (Test-Path (Join-Path $SdkPath 'tools/idf.py'))) {
    throw 'ESP-IDF is missing. Run firmware/tools/setup.ps1 first.'
}
$actualRevision = & git -C $SdkPath rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actualRevision -ne $expectedRevision) {
    throw "This probe requires ESP-IDF $expectedRevision; found $actualRevision"
}
# eim keeps toolchains and the Python env outside the SDK tree, so the generated
# activation script must be used instead of the SDK's own export.ps1.
if (-not (Test-Path $EimRegistry)) {
    throw "eim registry is missing at $EimRegistry. Run firmware/tools/setup.ps1 first."
}
$resolved = (Get-Item -LiteralPath $SdkPath).FullName
$entry = (Get-Content -LiteralPath $EimRegistry -Raw | ConvertFrom-Json).idfInstalled |
    Where-Object { $_.path -and ((Get-Item -LiteralPath $_.path -ErrorAction SilentlyContinue).FullName -ieq $resolved) } |
    Select-Object -First 1
if (-not $entry) {
    throw "No eim installation registered for $resolved. Run firmware/tools/setup.ps1 first."
}
. $entry.activationScript
if (-not $env:IDF_PYTHON_ENV_PATH) { throw 'ESP-IDF environment activation failed.' }
$script:ProbePython = Join-Path $env:IDF_PYTHON_ENV_PATH 'Scripts/python.exe'
$script:ProbeIdf = Join-Path $SdkPath 'tools/idf.py'
if (-not (Test-Path $script:ProbePython)) { throw 'ESP-IDF Python environment is missing.' }
