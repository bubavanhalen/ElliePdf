param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../testdata/generated'),
    [switch]$Include1Gb
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$environmentRoot = Join-Path ([IO.Path]::GetTempPath()) ('elliepdf-corpus-' + [guid]::NewGuid().ToString('N'))
try {
    # Keep dependencies isolated from preinstalled runner packages and pip settings.
    python -m venv $environmentRoot
    if ($LASTEXITCODE -ne 0) { throw 'Corpus Python environment creation failed.' }
    $python = Join-Path $environmentRoot 'Scripts/python.exe'
    & $python -m pip --isolated install --index-url https://pypi.org/simple --only-binary=:all: --require-hashes -r (Join-Path $PSScriptRoot 'test-corpus-requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Pinned corpus dependency installation failed.' }
    $generatorArguments = @((Join-Path $PSScriptRoot 'Generate-TestCorpus.py'), $OutputDirectory)
    if ($Include1Gb) { $generatorArguments += '--include-1gb' }
    & $python @generatorArguments
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic corpus generation failed.' }

    $manifest = Get-Content (Join-Path $repositoryRoot 'testdata/manifest.json') -Raw | ConvertFrom-Json
    foreach ($fixture in $manifest.fixtures) {
        $path = Join-Path $OutputDirectory $fixture.file
        if ($fixture.generation -and -not $Include1Gb -and -not (Test-Path -LiteralPath $path)) { continue }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing corpus fixture: $($fixture.id)" }
        if ($fixture.sizeBytes -and (Get-Item -LiteralPath $path).Length -ne $fixture.sizeBytes) { throw "Corpus size mismatch: $($fixture.id)" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $fixture.sha256) { throw "Corpus SHA-256 mismatch: $($fixture.id)" }
        Write-Host "Verified corpus fixture: $($fixture.id)"
    }
}
finally {
    if (Test-Path -LiteralPath $environmentRoot) {
        $resolved = [IO.Path]::GetFullPath($environmentRoot)
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'elliepdf-corpus-*') { throw 'Refusing cleanup outside the temporary corpus environment.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
