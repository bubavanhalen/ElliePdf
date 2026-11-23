[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath, [switch]$Execute)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw 'MSIX package was not found.' }
if (-not $Execute) { Write-Host 'Safe mode: WACK was not executed. Pass -Execute on a dedicated validation VM.'; exit 0 }
$appCert = Get-Command appcert.exe -ErrorAction SilentlyContinue
if ($null -eq $appCert) { throw 'appcert.exe was not found. Install the Windows SDK/Desktop App Certification Kit.' }
& $appCert.Source test -appxpackagepath (Resolve-Path -LiteralPath $PackagePath) -reportoutputpath (Join-Path (Split-Path -Parent $PackagePath) 'wack-report.xml')
if ($LASTEXITCODE -ne 0) { throw "WACK failed with exit code $LASTEXITCODE." }
Write-Host 'PASS WACK completed.'
