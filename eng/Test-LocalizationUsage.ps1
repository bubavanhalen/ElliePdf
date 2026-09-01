[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$resourcePath = Join-Path $repo 'Strings\en-US\Resources.resw'
[xml]$resources = Get-Content -LiteralPath $resourcePath
$keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($data in $resources.root.data) { [void]$keys.Add([string]$data.name) }
$failures = [Collections.Generic.List[string]]::new()

$xamlFiles = Get-ChildItem -LiteralPath $repo -Recurse -File -Filter *.xaml |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
$visibleAttributes = [Collections.Generic.HashSet[string]]::new(
    [string[]]@(
        'Text', 'Content', 'Header', 'Label', 'PlaceholderText', 'Title',
        'PrimaryButtonText', 'SecondaryButtonText', 'CloseButtonText',
        'ToolTip', 'Name', 'Description'),
    [StringComparer]::Ordinal)

foreach ($file in $xamlFiles) {
    [xml]$document = Get-Content -Raw -LiteralPath $file.FullName
    foreach ($node in $document.SelectNodes('//*')) {
        $uid = $node.Attributes | Where-Object {
            $_.LocalName -eq 'Uid' -and $_.NamespaceURI -eq 'http://schemas.microsoft.com/winfx/2006/xaml'
        } | Select-Object -First 1
        if ($null -ne $uid) {
            $prefix = [string]$uid.Value + '.'
            if (-not ($keys | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) } | Select-Object -First 1)) {
                $relative = [IO.Path]::GetRelativePath($repo, $file.FullName)
                $failures.Add("${relative}: x:Uid '$($uid.Value)' has no en-US resource property.")
            }
        }

        foreach ($attribute in $node.Attributes) {
            if (-not $visibleAttributes.Contains($attribute.LocalName)) { continue }
            $value = ([string]$attribute.Value).Trim()
            if ($value.Length -eq 0 -or $value.StartsWith('{', [StringComparison]::Ordinal)) { continue }
            if ($attribute.NamespaceURI -eq 'http://schemas.microsoft.com/winfx/2006/xaml') { continue }
            $relative = [IO.Path]::GetRelativePath($repo, $file.FullName)
            $failures.Add("${relative}: literal user-visible $($attribute.Name)='$value'. Use x:Uid/resources.")
        }
    }
}

$manifest = Get-Content -Raw -LiteralPath (Join-Path $repo 'Package.appxmanifest')
foreach ($match in [regex]::Matches($manifest, 'ms-resource:([A-Za-z0-9_.-]+)')) {
    if (-not $keys.Contains($match.Groups[1].Value)) {
        $failures.Add("Package.appxmanifest: missing resource '$($match.Groups[1].Value)'.")
    }
}

$codeRoots = @('Controls', 'Dialogs', 'Pages', 'ViewModels')
$codeFiles = foreach ($codeRoot in $codeRoots) {
    $path = Join-Path $repo $codeRoot
    if (Test-Path -LiteralPath $path) { Get-ChildItem -LiteralPath $path -Recurse -File -Filter *.cs }
}
$codeFiles += Get-Item -LiteralPath (Join-Path $repo 'MainPage.xaml.cs')
$codeFiles += Get-Item -LiteralPath (Join-Path $repo 'MainWindow.xaml.cs')
$codeFiles += Get-Item -LiteralPath (Join-Path $repo 'OrganizePage.xaml.cs')
$sinkPattern = [regex]::new(
    '(?m)\b(?:StatusMessage|Title|Content|Header|Text|PlaceholderText|PrimaryButtonText|SecondaryButtonText|CloseButtonText|SuggestedFileName)\s*=\s*\$?"|FileTypeChoices\.Add\(\s*"|AutomationProperties\.Set(?:Name|HelpText)\([^,]+,\s*\$?"')
foreach ($file in $codeFiles) {
    $text = Get-Content -Raw -LiteralPath $file.FullName
    foreach ($match in $sinkPattern.Matches($text)) {
        $line = 1 + ($text.Substring(0, $match.Index) -split "`n").Count - 1
        $relative = [IO.Path]::GetRelativePath($repo, $file.FullName)
        $failures.Add("${relative}:${line}: literal assigned to a user-visible UI sink; use AppResources.")
    }
}

if ($failures.Count -ne 0) {
    $failures | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    throw "Localization usage validation failed with $($failures.Count) finding(s)."
}

Write-Output "Localization usage passed: $($keys.Count) resources and $($xamlFiles.Count) XAML files."
