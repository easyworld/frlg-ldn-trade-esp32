[CmdletBinding()]
param(
    [ValidateSet('serial', 'auth', 'pia', 'room')][string]$Mode = 'serial',
    [Parameter(Mandatory)][ValidatePattern('^COM[1-9][0-9]*$')][string]$Port
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'host-diagnostic/C3.Diagnostic.csproj'
# Match the host project's locked Windows runtime graph.
& dotnet build $project -c Release -r win-x64 -p:RestoreLockedMode=true --nologo
if ($LASTEXITCODE -ne 0) { throw 'C3 diagnostic build failed.' }
& dotnet (Join-Path $PSScriptRoot 'host-diagnostic/bin/Release/net10.0/win-x64/C3.Diagnostic.dll') $Mode $Port
if ($LASTEXITCODE -ne 0) { throw 'C3 hardware diagnostic failed.' }
