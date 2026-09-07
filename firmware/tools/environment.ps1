param([string]$SdkPath = (Join-Path $env:USERPROFILE 'esp/esp-idf-v6.1'))
$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
$expectedRevision = 'fff9895c82d744c7237be8847347bdd1b07c6643'
if (-not (Test-Path (Join-Path $SdkPath 'export.ps1'))) {
    throw 'ESP-IDF is missing. Run firmware/tools/setup.ps1 first.'
}
$actualRevision = & git -C $SdkPath rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actualRevision -ne $expectedRevision) {
    throw "This probe requires ESP-IDF $expectedRevision; found $actualRevision"
}
. (Join-Path $SdkPath 'export.ps1')
if (-not $env:IDF_PYTHON_ENV_PATH) { throw 'ESP-IDF environment activation failed.' }
$script:ProbePython = Join-Path $env:IDF_PYTHON_ENV_PATH 'Scripts/python.exe'
$script:ProbeIdf = Join-Path $SdkPath 'tools/idf.py'
if (-not (Test-Path $script:ProbePython)) { throw 'ESP-IDF Python environment is missing.' }
