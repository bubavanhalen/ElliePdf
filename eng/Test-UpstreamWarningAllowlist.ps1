[CmdletBinding()]
param([string]$Path = (Join-Path $PSScriptRoot 'upstream-warning-allowlist.json'))
$ErrorActionPreference = 'Stop'
$document = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
$today = [DateTime]::UtcNow.Date
foreach ($warning in @($document.warnings)) {
    foreach ($property in @('id', 'issueUrl', 'owner', 'expiresOn')) {
        if ([string]::IsNullOrWhiteSpace([string]$warning.$property)) { throw "Allowlisted warning is missing '$property'." }
    }
    $expiry = [DateTime]::ParseExact($warning.expiresOn, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    if ($expiry.Date -lt $today) { throw "Allowlisted warning '$($warning.id)' expired on $($warning.expiresOn)." }
}
Write-Output "Upstream warning allowlist valid: $(@($document.warnings).Count) entries."
