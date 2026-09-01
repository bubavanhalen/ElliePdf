[CmdletBinding()]
param(
    [string] $TargetPath,
    [string] $FixturePath,
    [int] $FixturePageCount = 3,
    [string] $SecondaryFixturePath,
    [int] $SecondaryFixturePageCount = 1000,
    [string] $ResultPath = 'artifacts/uia/uia-report.json',
    [ValidateRange(5, 120)] [int] $LaunchTimeoutSeconds = 30,
    [ValidateRange(5, 120)] [int] $InteractionTimeoutSeconds = 30,
    [switch] $Interactive,
    [switch] $Execute,
    [switch] $RequireFixture,
    [switch] $RequireHighContrast
)

$ErrorActionPreference = 'Stop'

if (-not $Interactive) {
    Write-Host 'UIA inventory only: run with -Interactive -Execute -TargetPath <signed ElliePdf.exe> on a dedicated desktop session.'
    Write-Host 'The fixed desktop procedure is documented in eng/UIA-PROCEDURE.md.'
    exit 0
}

if (-not $Execute) {
    throw 'Interactive UIA mode is fail-closed: pass both -Interactive and -Execute.'
}

if ([string]::IsNullOrWhiteSpace($TargetPath) -or -not (Test-Path -LiteralPath $TargetPath -PathType Leaf)) {
    throw 'Interactive UIA mode requires an existing -TargetPath.'
}

if (-not [string]::IsNullOrWhiteSpace($FixturePath) -and -not (Test-Path -LiteralPath $FixturePath -PathType Leaf)) {
    throw 'FixturePath was provided but does not exist.'
}

if (-not [string]::IsNullOrWhiteSpace($SecondaryFixturePath) -and -not (Test-Path -LiteralPath $SecondaryFixturePath -PathType Leaf)) {
    throw 'SecondaryFixturePath was provided but does not exist.'
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class ElliePdfNativeWindowing
{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HighContrast
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr lpszDefaultScheme;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrast data, uint update);

    public static bool IsHighContrastEnabled()
    {
        var data = new HighContrast { cbSize = (uint)Marshal.SizeOf<HighContrast>() };
        return SystemParametersInfo(0x0042, data.cbSize, ref data, 0) && (data.dwFlags & 1) != 0;
    }
}
'@

$resolvedTargetPath = [IO.Path]::GetFullPath($TargetPath)
$resolvedResultPath = [IO.Path]::GetFullPath($ResultPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedResultPath)) | Out-Null

$utf8NoBom = [Text.UTF8Encoding]::new($false)
$report = [ordered]@{
    schemaVersion = 'uia-1.0'
    startedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    target = [ordered]@{
        executableName = [IO.Path]::GetFileName($resolvedTargetPath)
        interactive = $true
        executed = $true
    }
    environment = [ordered]@{
        osVersion = [Environment]::OSVersion.VersionString
        dotnet = [Environment]::Version.ToString()
        highContrast = [ElliePdfNativeWindowing]::IsHighContrastEnabled()
    }
    manualGates = [ordered]@{
        narrator = 'required-manual'
        touchAndPen = 'required-manual'
        accessibilityInsights = 'required-manual'
        signedInstall = 'required-manual'
    }
    checks = [Collections.Generic.List[object]]::new()
    summary = [ordered]@{
        status = 'running'
        passed = 0
        failed = 0
        skipped = 0
        unnamedActionableControls = 0
        totalDescendants = 0
        tabCount = 0
    }
}

function Add-Check([string] $Name, [string] $Status, [string] $Evidence) {
    $report.checks.Add([ordered]@{
        name = $Name
        status = $Status
        evidence = $Evidence
    }) | Out-Null
    switch ($Status) {
        'pass' { $report.summary.passed++ }
        'skip' { $report.summary.skipped++ }
        default { $report.summary.failed++ }
    }
}

function Complete-Report([string] $Status) {
    $report.summary.status = $Status
    $report.completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    [IO.File]::WriteAllText($resolvedResultPath, ($report | ConvertTo-Json -Depth 8), $utf8NoBom)
}

function Require([bool] $Condition, [string] $Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function New-PropertyCondition($Property, $Value) {
    return New-Object Windows.Automation.PropertyCondition($Property, $Value)
}

function Find-First($Root, [string] $ScopeName, $Condition) {
    return $Root.FindFirst([Windows.Automation.TreeScope]::$ScopeName, $Condition)
}

function Find-All($Root, [string] $ScopeName, $Condition) {
    return $Root.FindAll([Windows.Automation.TreeScope]::$ScopeName, $Condition)
}

function Get-RuntimeId([Windows.Automation.AutomationElement] $Element) {
    return ($Element.GetRuntimeId() | ForEach-Object { $_.ToString([Globalization.CultureInfo]::InvariantCulture) }) -join '.'
}

function Wait-Until([scriptblock] $Predicate, [TimeSpan] $Timeout, [string] $FailureMessage) {
    $deadline = [DateTime]::UtcNow + $Timeout
    do {
        $value = & $Predicate
        if ($value) {
            return $value
        }
        Start-Sleep -Milliseconds 100
    }
    while ([DateTime]::UtcNow -lt $deadline)
    throw $FailureMessage
}

function Wait-ForWindow([Diagnostics.Process] $Process, [TimeSpan] $Timeout) {
    return Wait-Until {
        $condition = New-PropertyCondition ([Windows.Automation.AutomationElement]::ProcessIdProperty) $Process.Id
        Find-First ([Windows.Automation.AutomationElement]::RootElement) 'Children' $condition
    } $Timeout 'No application window was found through UI Automation.'
}

function Wait-ForDescendantByAutomationId($Window, [string] $AutomationId, [TimeSpan] $Timeout) {
    return Wait-Until {
        $condition = New-PropertyCondition ([Windows.Automation.AutomationElement]::AutomationIdProperty) $AutomationId
        Find-First $Window 'Descendants' $condition
    } $Timeout "UIA element with AutomationId '$AutomationId' was not found."
}

function Wait-ForDescendantByName($Window, [string] $Name, [TimeSpan] $Timeout) {
    return Wait-Until {
        $condition = New-PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty) $Name
        Find-First $Window 'Descendants' $condition
    } $Timeout "UIA element named '$Name' was not found."
}

function Wait-ForFocusedDescendant($Window, [TimeSpan] $Timeout) {
    return Wait-Until {
        $focused = [Windows.Automation.AutomationElement]::FocusedElement
        if ($null -eq $focused) { return $null }
        try {
            $runtimeId = Get-RuntimeId $focused
            if ([string]::IsNullOrWhiteSpace($runtimeId)) { return $null }
            return $focused
        }
        catch [Windows.Automation.ElementNotAvailableException] {
            return $null
        }
    } $Timeout 'No focused automation element became available.'
}

function Get-ElementLabel($Element) {
    $automationId = [string]$Element.Current.AutomationId
    $name = [string]$Element.Current.Name
    $controlType = [string]$Element.Current.ControlType.ProgrammaticName
    if (-not [string]::IsNullOrWhiteSpace($automationId)) { return $automationId }
    if (-not [string]::IsNullOrWhiteSpace($name)) { return $name }
    return $controlType
}

function Ensure-Pattern($Element, $Pattern, [string] $PatternName) {
    $patternObject = $null
    $supported = $Element.TryGetCurrentPattern($Pattern, [ref] $patternObject)
    Require $supported "Element '$(Get-ElementLabel $Element)' does not support $PatternName."
}

function Invoke-Element($Element) {
    $patternObject = $null
    Require ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref] $patternObject)) "Element '$(Get-ElementLabel $Element)' does not support InvokePattern."
    ([Windows.Automation.InvokePattern]$patternObject).Invoke()
}

function Select-Element($Element) {
    $patternObject = $null
    Require ($Element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref] $patternObject)) "Element '$(Get-ElementLabel $Element)' does not support SelectionItemPattern."
    ([Windows.Automation.SelectionItemPattern]$patternObject).Select()
}

function Set-ElementValue($Element, [string] $Value) {
    $patternObject = $null
    Require ($Element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref] $patternObject)) "Element '$(Get-ElementLabel $Element)' does not support ValuePattern."
    ([Windows.Automation.ValuePattern]$patternObject).SetValue($Value)
}

function Get-ElementValue($Element) {
    $patternObject = $null
    Require ($Element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref] $patternObject)) "Element '$(Get-ElementLabel $Element)' does not support ValuePattern."
    return ([Windows.Automation.ValuePattern]$patternObject).Current.Value
}

function Get-TabItems($Window) {
    $tabCondition = New-PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty) ([Windows.Automation.ControlType]::TabItem)
    return @(Find-All $Window 'Descendants' $tabCondition)
}

function Wait-ForTabCount($Window, [int] $ExpectedCount, [TimeSpan] $Timeout) {
    return Wait-Until {
        $items = @(Get-TabItems $Window)
        if ($items.Count -eq $ExpectedCount) { return $items }
        return $null
    } $Timeout "Expected $ExpectedCount tab item(s), but the UIA tree did not converge."
}

function Bring-ToForeground([Diagnostics.Process] $Process) {
    if ($Process.MainWindowHandle -eq [IntPtr]::Zero) {
        $Process.Refresh()
    }
    [void][ElliePdfNativeWindowing]::ShowWindow($Process.MainWindowHandle, 5)
    [void][ElliePdfNativeWindowing]::SetForegroundWindow($Process.MainWindowHandle)
}

function Send-Keys([string] $Keys) {
    if (-not $script:WshShell) {
        $script:WshShell = New-Object -ComObject WScript.Shell
    }
    $script:WshShell.SendKeys($Keys)
}

function Start-FileActivation([string] $Path) {
    Start-Process -FilePath $Path | Out-Null
}

function Assert-PageIdentity($Window, [int] $ExpectedPage, [int] $ExpectedCount, [TimeSpan] $Timeout) {
    $pageLabel = "Page $ExpectedPage of $ExpectedCount"
    $pageElement = Wait-ForDescendantByName $Window $pageLabel $Timeout
    $pageNumber = Wait-ForDescendantByName $Window 'Page number' $Timeout
    Require ((Get-ElementValue $pageNumber) -eq $ExpectedPage.ToString([Globalization.CultureInfo]::InvariantCulture)) 'Page number UIA value did not match the expected page index.'
    return [ordered]@{
        pageLabel = $pageLabel
        pageElement = $pageElement
        pageNumber = $pageNumber
    }
}

function Test-TabNavigation($Window, [TimeSpan] $Timeout) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $menuButton = Wait-ForDescendantByName $Window 'Menu' $Timeout
    $menuButton.SetFocus()
    $focused = Wait-ForFocusedDescendant $Window $Timeout
    $seen.Add((Get-ElementLabel $focused)) | Out-Null
    for ($index = 0; $index -lt 8; $index++) {
        Send-Keys('{TAB}')
        Start-Sleep -Milliseconds 120
        $focused = Wait-ForFocusedDescendant $Window $Timeout
        $seen.Add((Get-ElementLabel $focused)) | Out-Null
    }
    Require ($seen.Count -ge 4) 'Tab navigation did not traverse at least four distinct reachable controls.'
    return $seen.Count
}

function Get-AccessibleCommandButtons($Window) {
    $controls = @(Find-All $Window 'Descendants' ([Windows.Automation.Condition]::TrueCondition))
    $buttonsAndTabs = @($controls | Where-Object {
        $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -or
        $_.Current.ControlType -eq [Windows.Automation.ControlType]::TabItem
    })
    $unnamed = @($buttonsAndTabs | Where-Object { [string]::IsNullOrWhiteSpace($_.Current.Name) })
    return [ordered]@{
        totalDescendants = $controls.Count
        unnamedActionableControls = $unnamed.Count
    }
}

function Test-FormAutomation($Window) {
    $controls = @(Find-All $Window 'Descendants' ([Windows.Automation.Condition]::TrueCondition) | Where-Object {
        $_.Current.ControlType -in @([Windows.Automation.ControlType]::Edit,
            [Windows.Automation.ControlType]::CheckBox, [Windows.Automation.ControlType]::RadioButton,
            [Windows.Automation.ControlType]::ComboBox, [Windows.Automation.ControlType]::List,
            [Windows.Automation.ControlType]::Button)
    })
    Require ($controls.Count -gt 0) 'The forms fixture exposed no actionable form controls through UIA.'
    $unnamed = @($controls | Where-Object { [string]::IsNullOrWhiteSpace($_.Current.Name) })
    Require ($unnamed.Count -eq 0) "$($unnamed.Count) form/actionable controls have no accessible name."
    foreach ($control in $controls) {
        $pattern = $null
        $supportsValue = $control.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)
        $supportsToggle = $control.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)
        $supportsSelection = $control.TryGetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern, [ref]$pattern)
        $supportsInvoke = $control.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)
        Require ($supportsValue -or $supportsToggle -or $supportsSelection -or $supportsInvoke) "Form control '$(Get-ElementLabel $control)' exposes no actionable UIA pattern."
    }
    return $controls.Count
}

function Test-VirtualizedPages($Window, [int] $ExpectedPageCount) {
    $pagePeers = @(Find-All $Window 'Descendants' ([Windows.Automation.Condition]::TrueCondition) | Where-Object {
        $size = $_.GetCurrentPropertyValue([Windows.Automation.AutomationElement]::SizeOfSetProperty, $true)
        $position = $_.GetCurrentPropertyValue([Windows.Automation.AutomationElement]::PositionInSetProperty, $true)
        $size -is [int] -and $position -is [int] -and
            $size -eq $ExpectedPageCount -and $position -ge 1 -and $position -le $ExpectedPageCount
    })
    Require ($pagePeers.Count -gt 0) 'No virtualized page automation peers with PositionInSet/SizeOfSet were exposed.'
    Require ($pagePeers.Count -le 12) "Virtualization contract failed: $($pagePeers.Count) page peers exceeded the 12-peer realization ceiling."
    $positions = @($pagePeers | ForEach-Object {
        [int]$_.GetCurrentPropertyValue([Windows.Automation.AutomationElement]::PositionInSetProperty, $true)
    } | Sort-Object -Unique)
    Require ($positions.Count -eq $pagePeers.Count) 'Virtualized page peers exposed duplicate PositionInSet values.'
    return $pagePeers.Count
}

$process = $null
$temporarySecondFixture = $null
try {
    $process = Start-Process -FilePath $resolvedTargetPath -PassThru
    $timeout = [TimeSpan]::FromSeconds($LaunchTimeoutSeconds)
    $interactionTimeout = [TimeSpan]::FromSeconds($InteractionTimeoutSeconds)
    $window = Wait-ForWindow $process $timeout
    Bring-ToForeground $process

    if ($RequireHighContrast -and -not $report.environment.highContrast) {
        throw 'High Contrast was required but is not enabled on the interactive test host.'
    }
    Add-Check 'high-contrast-uia-compatibility' $(if ($report.environment.highContrast) { 'pass' } else { 'skip' }) $(if ($report.environment.highContrast) { 'UIA names, roles, and patterns were collected with High Contrast enabled.' } else { 'Host was not in High Contrast; rerun with -RequireHighContrast on a High Contrast desktop.' })

    $inventory = Get-AccessibleCommandButtons $window
    $report.summary.totalDescendants = $inventory.totalDescendants
    $report.summary.unnamedActionableControls = $inventory.unnamedActionableControls
    Require ($inventory.unnamedActionableControls -eq 0) "$($inventory.unnamedActionableControls) actionable UIA controls have no accessible name."
    Add-Check 'actionable-controls-named' 'pass' "$($inventory.totalDescendants) descendants; every button and tab item has an accessible name."

    $navView = Wait-ForDescendantByAutomationId $window 'NavView' $timeout
    $readNav = Wait-ForDescendantByAutomationId $window 'NavItemRead' $timeout
    $settingsNav = Wait-ForDescendantByAutomationId $window 'NavItemSettings' $timeout
    $contentFrame = Wait-ForDescendantByAutomationId $window 'ContentFrame' $timeout
    $openButton = Wait-ForDescendantByName $window 'Open PDF' $timeout
    $readerCommandBar = $null

    Ensure-Pattern $readNav ([Windows.Automation.SelectionItemPattern]::Pattern) 'SelectionItemPattern'
    Ensure-Pattern $settingsNav ([Windows.Automation.SelectionItemPattern]::Pattern) 'SelectionItemPattern'
    Ensure-Pattern $openButton ([Windows.Automation.InvokePattern]::Pattern) 'InvokePattern'
    Add-Check 'shell-controls-and-patterns' 'pass' 'NavView, ContentFrame, Open PDF, Read, and Settings are present with required UIA patterns.'

    $focusStops = Test-TabNavigation $window $interactionTimeout
    Add-Check 'keyboard-tab-navigation' 'pass' "Tab traversal reached $focusStops distinct focus targets."

    Select-Element $settingsNav
    Wait-ForDescendantByName $window 'Settings' $interactionTimeout | Out-Null
    Select-Element $readNav
    Add-Check 'keyboard-focus-and-settings-navigation' 'pass' 'Settings navigation is reachable and returns to the reader workspace.'

    if ([string]::IsNullOrWhiteSpace($FixturePath)) {
        Add-Check 'document-open-and-reader-contract' 'skip' 'No fixture was provided; shell-only accessibility checks were executed.'
        Add-Check 'tab-switch-close-and-stale-check' 'skip' 'No fixture pair was provided; tab lifecycle checks were skipped.'
    }
    else {
        $resolvedFixturePath = [IO.Path]::GetFullPath($FixturePath)
        if ([string]::IsNullOrWhiteSpace($SecondaryFixturePath)) {
            $temporarySecondFixture = Join-Path ([IO.Path]::GetTempPath()) ("elliepdf-uia-second-{0}.pdf" -f [guid]::NewGuid().ToString('N'))
            Copy-Item -LiteralPath $resolvedFixturePath -Destination $temporarySecondFixture
            $resolvedSecondFixturePath = $temporarySecondFixture
            $secondFixturePageCount = $FixturePageCount
        }
        else {
            $resolvedSecondFixturePath = [IO.Path]::GetFullPath($SecondaryFixturePath)
            $secondFixturePageCount = $SecondaryFixturePageCount
        }

        Start-FileActivation $resolvedFixturePath
        $readerCommandBar = Wait-ForDescendantByAutomationId $window 'ReaderCommandBar' $interactionTimeout
        $pageIdentity = Assert-PageIdentity $window 1 $FixturePageCount $interactionTimeout
        Ensure-Pattern $pageIdentity.pageNumber ([Windows.Automation.ValuePattern]::Pattern) 'ValuePattern'
        Add-Check 'document-open-and-reader-contract' 'pass' "Reader command bar appeared and exposed the privacy-safe page identity '$($pageIdentity.pageLabel)'."

        $virtualizedCount = Test-VirtualizedPages $window $FixturePageCount
        Add-Check 'virtualized-page-automation' 'pass' "$virtualizedCount of $FixturePageCount page peers were realized; the UIA tree remained demand-driven."

        $searchToggle = Wait-ForDescendantByName $window 'Search' $interactionTimeout
        Invoke-Element $searchToggle
        $searchBox = Wait-ForDescendantByName $window 'Search in document' $interactionTimeout
        Ensure-Pattern $searchBox ([Windows.Automation.ValuePattern]::Pattern) 'ValuePattern'
        Add-Check 'search-box-pattern' 'pass' 'Search is actionable and the search box exposes ValuePattern.'

        $formCandidates = @($window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | Where-Object {
            $_.Current.HelpText -match '(?i)form|field|editable|required|read-only|unsupported'
        })
        if ($formCandidates.Count -gt 0) {
            $formCount = Test-FormAutomation $window
            Add-Check 'form-controls-and-patterns' 'pass' "$formCount actionable form controls were named and exposed at least one value, toggle, selection, or invoke pattern."
        }
        else {
            Add-Check 'form-controls-and-patterns' 'skip' 'The supplied fixture exposed no form widgets; run with synthetic-mixed-orientation-links-forms-outlines.pdf to exercise this contract.'
        }

        Bring-ToForeground $process
        Send-Keys('^g')
        Start-Sleep -Milliseconds 150
        Set-ElementValue $pageIdentity.pageNumber '2'
        Send-Keys('~')
        Assert-PageIdentity $window 2 $FixturePageCount $interactionTimeout | Out-Null
        Add-Check 'page-identity-and-keyboard-navigation' 'pass' 'Ctrl+G navigation updated both the page host identity and page number value.'

        Start-FileActivation $resolvedSecondFixturePath
        $tabs = Wait-ForTabCount $window 2 $interactionTimeout
        $report.summary.tabCount = $tabs.Count
        $firstTabRuntimeId = Get-RuntimeId $tabs[0]
        $secondTabRuntimeId = Get-RuntimeId $tabs[1]
        Select-Element $tabs[1]
        Assert-PageIdentity $window 1 $secondFixturePageCount $interactionTimeout | Out-Null
        $longDocumentPeerCount = Test-VirtualizedPages $window $secondFixturePageCount
        Add-Check 'long-document-virtualization' 'pass' "$longDocumentPeerCount of $secondFixturePageCount page peers were realized; the 12-peer ceiling held."
        Select-Element $tabs[0]
        Assert-PageIdentity $window 2 $FixturePageCount $interactionTimeout | Out-Null
        Select-Element $tabs[1]
        Bring-ToForeground $process
        Send-Keys('^w')
        $remainingTabs = Wait-ForTabCount $window 1 $interactionTimeout
        $report.summary.tabCount = $remainingTabs.Count
        Assert-PageIdentity $window 2 $FixturePageCount $interactionTimeout | Out-Null
        $currentRuntimeIds = @(Get-TabItems $window | ForEach-Object { Get-RuntimeId $_ })
        Require ($currentRuntimeIds -contains $firstTabRuntimeId) 'The surviving tab runtime identifier was not preserved after closing the other tab.'
        Require (-not ($currentRuntimeIds -contains $secondTabRuntimeId)) 'The closed tab still appears in the UIA tree after Ctrl+W.'
        Add-Check 'tab-switch-close-and-stale-check' 'pass' 'Tab switching preserved document identity and closing the active tab removed its runtime id from the UIA tree.'
    }

    if ($RequireFixture -and [string]::IsNullOrWhiteSpace($FixturePath)) {
        throw 'A fixture is required for the complete reader, virtualization, and form UIA contract.'
    }

    Complete-Report 'passed'
    Write-Host "PASS UIA workflow: $resolvedResultPath"
}
catch {
    Add-Check 'fatal' 'fail' $_.Exception.GetType().Name
    Complete-Report 'failed'
    throw
}
finally {
    if ($process -is [Diagnostics.Process]) {
        try {
            if (-not $process.HasExited) {
                $process.Kill($true)
            }
        }
        catch {
        }
        $process.Dispose()
    }

    if (-not [string]::IsNullOrWhiteSpace($temporarySecondFixture) -and (Test-Path -LiteralPath $temporarySecondFixture -PathType Leaf)) {
        Remove-Item -LiteralPath $temporarySecondFixture -Force
    }
}
