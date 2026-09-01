[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$SymbolsPath = (Join-Path $PSScriptRoot '..\artifacts\symbols\win-x64')
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'SourceLinkVerifier\SourceLinkVerifier.csproj'
dotnet run --project $project --configuration $Configuration -- --self-test
if ($LASTEXITCODE -ne 0) { throw "SourceLink verifier self-test failed ($LASTEXITCODE)." }
dotnet run --project $project --configuration $Configuration -- (Resolve-Path $SymbolsPath).Path
if ($LASTEXITCODE -ne 0) { throw "SourceLink verification failed ($LASTEXITCODE)." }
