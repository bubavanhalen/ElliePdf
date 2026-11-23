$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$base = Get-Content (Join-Path $root 'Strings/en-US/Resources.resw')
$baseKeys = @($base.root.data | ForEach-Object name)
foreach ($locale in 'qps-ploc', 'qps-plocm') {
    $path = Join-Path $root "Strings/$locale/Resources.resw"
    [xml]$doc = Get-Content $path
    $keys = @($doc.root.data | ForEach-Object name)
    foreach ($item in $base.root.data) {
        if ($item.name -in $keys) { continue }
        $value = [string]$item.value
        if ($locale -eq 'qps-ploc') { $value = "[!! $value !!]" }
        else { $value = "`u{202B}[$value]`u{202C}" }
        $node = $doc.CreateElement('data')
        $node.SetAttribute('name', $item.name)
        $node.SetAttribute('xml:space', 'preserve')
        $valueNode = $doc.CreateElement('value')
        $valueNode.InnerText = $value
        [void]$node.AppendChild($valueNode)
        [void]$doc.root.AppendChild($node)
    }
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
    $writer = [System.Xml.XmlWriter]::Create($path, $settings)
    $doc.Save($writer)
    $writer.Dispose()
}
