[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot 'Test-DevelopmentPayload.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("ElliePdf-DevelopmentPayloadTests-{0}" -f [guid]::NewGuid())
$passed = 0

function New-SyntheticPe([string]$Path, [ValidateSet('x64', 'arm64')][string]$Architecture) {
    $bytes = [byte[]]::new(128)
    $bytes[0] = 0x4D
    $bytes[1] = 0x5A
    [BitConverter]::GetBytes([uint32]0x40).CopyTo($bytes, 0x3C)
    [BitConverter]::GetBytes([uint32]0x00004550).CopyTo($bytes, 0x40)
    $machine = if ($Architecture -eq 'x64') { [uint16]0x8664 } else { [uint16]0xAA64 }
    [BitConverter]::GetBytes($machine).CopyTo($bytes, 0x44)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function ConvertTo-XmlAttribute([string]$Value) {
    return [Security.SecurityElement]::Escape($Value)
}

function New-ValidFixture([string]$Name, [ValidateSet('x64', 'arm64')][string]$Architecture) {
    $root = Join-Path $testRoot $Name
    $payload = Join-Path $root 'payload'
    [IO.Directory]::CreateDirectory($payload) | Out-Null

    $worker = Join-Path $payload 'ElliePdf.Pdfium.Worker.exe'
    $pdfium = Join-Path $payload 'pdfium.dll'
    $runtimeConfig = Join-Path $payload 'ElliePdf.Pdfium.Worker.runtimeconfig.json'
    $coreclr = Join-Path $payload 'coreclr.dll'
    New-SyntheticPe $worker $Architecture
    New-SyntheticPe $pdfium $Architecture
    [IO.File]::WriteAllText($runtimeConfig, '{}')
    [IO.File]::WriteAllText($coreclr, 'synthetic')

    $entries = [Collections.Generic.List[object]]::new()
    $entries.Add([pscustomobject]@{ Source = $worker; PackagePath = 'PdfWorker\ElliePdf.Pdfium.Worker.exe' })
    $entries.Add([pscustomobject]@{ Source = $runtimeConfig; PackagePath = 'PdfWorker\ElliePdf.Pdfium.Worker.runtimeconfig.json' })
    $entries.Add([pscustomobject]@{ Source = $coreclr; PackagePath = 'PdfWorker\coreclr.dll' })
    $entries.Add([pscustomobject]@{ Source = $pdfium; PackagePath = 'PdfWorker\pdfium.dll' })
    return [pscustomobject]@{ Root = $root; Recipe = (Join-Path $root 'ElliePdf.build.appxrecipe'); Entries = $entries }
}

function Write-Recipe($Fixture) {
    $nodes = $Fixture.Entries | ForEach-Object {
        "    <AppxPackagedFile Include=`"$(ConvertTo-XmlAttribute $_.Source)`" PackagePath=`"$(ConvertTo-XmlAttribute $_.PackagePath)`" />"
    }
    $xml = "<Project>`n  <ItemGroup>`n$($nodes -join "`n")`n  </ItemGroup>`n</Project>"
    [IO.File]::WriteAllText($Fixture.Recipe, $xml)
}

function Assert-Passes([string]$Name, [scriptblock]$Action) {
    try { & $Action | Out-Null }
    catch { throw "FAIL ${Name}: expected success, got: $($_.Exception.Message)" }
    $script:passed++
    Write-Host "PASS $Name"
}

function Assert-Fails([string]$Name, [string]$ExpectedMessage, [scriptblock]$Action) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "FAIL ${Name}: expected error containing '$ExpectedMessage', got: $($_.Exception.Message)"
        }
        $script:passed++
        Write-Host "PASS $Name"
        return
    }
    throw "FAIL ${Name}: expected failure containing '$ExpectedMessage'."
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null

    foreach ($architecture in @('x64', 'arm64')) {
        $fixture = New-ValidFixture "valid-$architecture" $architecture
        Write-Recipe $fixture
        Assert-Passes "valid $architecture payload" { & $validator -RecipePath $fixture.Recipe -ExpectedArchitecture $architecture }
    }

    $fixture = New-ValidFixture 'missing-worker-source' 'x64'
    $fixture.Entries[0].Source = Join-Path $fixture.Root 'missing-worker.exe'
    Write-Recipe $fixture
    Assert-Fails 'missing worker source' 'Development payload source does not exist' { & $validator -RecipePath $fixture.Recipe -ExpectedArchitecture x64 }

    $fixture = New-ValidFixture 'missing-runtime-source' 'x64'
    $fixture.Entries[1].Source = Join-Path $fixture.Root 'missing-runtimeconfig.json'
    Write-Recipe $fixture
    Assert-Fails 'missing runtime source' 'Development payload source does not exist' { & $validator -RecipePath $fixture.Recipe -ExpectedArchitecture x64 }

    $fixture = New-ValidFixture 'wrong-architecture' 'arm64'
    Write-Recipe $fixture
    Assert-Fails 'wrong architecture' 'is arm64, expected x64' { & $validator -RecipePath $fixture.Recipe -ExpectedArchitecture x64 }

    $fixture = New-ValidFixture 'root-pdfium' 'x64'
    $fixture.Entries.Add([pscustomobject]@{ Source = $fixture.Entries[3].Source; PackagePath = 'pdfium.dll' })
    Write-Recipe $fixture
    Assert-Fails 'root pdfium' 'must not place pdfium.dll at the package root' { & $validator -RecipePath $fixture.Recipe -ExpectedArchitecture x64 }

    $fixture = New-ValidFixture 'publish-subtree' 'x64'
    $fixture.Entries[3].PackagePath = 'PdfWorker\publish\pdfium.dll'
    Write-Recipe $fixture
    Assert-Fails 'publish subtree' 'must not source files from a publish subtree' { & $validator -RecipePath $fixture.Recipe -ExpectedArchitecture x64 }

    $fixture = New-ValidFixture 'duplicate-required-entry' 'x64'
    $fixture.Entries.Add([pscustomobject]@{ Source = $fixture.Entries[2].Source; PackagePath = $fixture.Entries[2].PackagePath })
    Write-Recipe $fixture
    Assert-Fails 'duplicate required entry' "exactly one 'PdfWorker/coreclr.dll' entry; found 2" { & $validator -RecipePath $fixture.Recipe -ExpectedArchitecture x64 }

    $malformed = Join-Path $testRoot 'malformed.appxrecipe'
    [IO.File]::WriteAllText($malformed, '<Project><ItemGroup>')
    Assert-Fails 'malformed recipe' 'unexpected end of file' { & $validator -RecipePath $malformed -ExpectedArchitecture x64 }

    Write-Host "PASS development payload regression suite: $passed tests."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedRoot = (Resolve-Path -LiteralPath $testRoot).Path
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolvedRoot) -notlike 'ElliePdf-DevelopmentPayloadTests-*') {
            throw "Refusing cleanup outside the generated test directory: $resolvedRoot"
        }
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
