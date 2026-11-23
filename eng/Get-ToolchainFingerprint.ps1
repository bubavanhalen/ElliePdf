[CmdletBinding()]
param([string]$OutputPath = (Join-Path $PWD 'toolchain-fingerprint.json'))
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sdkManifest = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'global.json') | ConvertFrom-Json
$sdkInfo = dotnet --info | Out-String
$lockFiles = Get-ChildItem -LiteralPath $repoRoot -Filter packages.lock.json -Recurse -File | Sort-Object FullName
$toolManifestPath = Join-Path $repoRoot '.config\dotnet-tools.json'
$locks = @($lockFiles | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($repoRoot.Length + 1); sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash }
})
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $dotnetInfoHash = ([BitConverter]::ToString($sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($sdkInfo)))).Replace('-', '')
}
finally { $sha256.Dispose() }
$fingerprint = [ordered]@{
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    sdk = $sdkManifest.sdk
    dotnetInfoSha256 = $dotnetInfoHash
    windowsMinimum = '10.0.26100.0'
    runtimes = @('win-x64', 'win-arm64')
    localToolManifest = if (Test-Path -LiteralPath $toolManifestPath -PathType Leaf) { [ordered]@{ path = '.config/dotnet-tools.json'; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $toolManifestPath).Hash } } else { $null }
    packageLockFiles = $locks
}
$fingerprint | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Toolchain fingerprint written to $OutputPath"
