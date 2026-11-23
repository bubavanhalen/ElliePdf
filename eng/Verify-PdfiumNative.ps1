[CmdletBinding()]
param(
    [string] $PackageRoot = (Join-Path $env:USERPROFILE '.nuget\packages\bblanchon.pdfium.win32\154.0.8021'),
    [string[]] $RuntimeIdentifiers = @('win-x64', 'win-arm64')
)

$ErrorActionPreference = 'Stop'
$expected = @{
    'win-x64'   = @{ Length = 7262720; Sha256 = '2A9031FA88F412147C3BC7115054550048C724DB6EA70298B6C6B0D13E513882'; Machine = 0x8664 }
    'win-arm64' = @{ Length = 6705152; Sha256 = 'B8A41647AC18C039C4A9CE4F00C1D71A08133EDF92531A9C7903FD985A04DB73'; Machine = 0xAA64 }
}
$requiredExports = @(
    'FPDF_InitLibrary','FPDF_DestroyLibrary','FPDF_LoadDocument','FPDF_LoadCustomDocument','FPDF_CloseDocument','FPDF_GetPageCount',
    'FPDF_GetMetaText','FPDF_GetFileVersion','FPDF_GetDocPermissions','FPDF_GetSecurityHandlerRevision','FPDF_GetFormType',
    'FPDF_LoadPage','FPDF_ClosePage','FPDF_GetPageWidthF','FPDF_GetPageHeightF','FPDFBitmap_Create',
    'FPDFBitmap_FillRect','FPDFBitmap_GetBuffer','FPDFBitmap_GetStride','FPDFBitmap_Destroy','FPDF_RenderPageBitmap',
    'FPDFDOC_InitFormFillEnvironment','FPDFDOC_ExitFormFillEnvironment','FORM_OnAfterLoadPage','FORM_OnBeforeClosePage','FORM_OnLButtonDown','FORM_OnLButtonUp',
    'FPDF_FFLDraw','FPDFPage_GetRotation','FPDFPage_SetRotation','FPDFPage_GenerateContent','FPDFPage_Delete',
    'FPDFPageObj_CreateNewPath','FPDFPath_LineTo','FPDFPath_SetDrawMode','FPDFPageObj_SetStrokeColor','FPDFPageObj_SetStrokeWidth','FPDFPageObj_SetLineJoin','FPDFPageObj_SetLineCap',
    'FPDFPage_New','FPDFPageObj_NewImageObj','FPDFImageObj_SetBitmap','FPDFPageObj_SetMatrix','FPDFPage_InsertObject','FPDFPageObj_Destroy',
    'FPDF_CreateNewDocument','FPDF_ImportPagesByIndex','FPDF_CopyViewerPreferences','FPDF_SaveAsCopy','FPDF_GetLastError',
    'FPDFText_LoadPage','FPDFText_ClosePage','FPDFText_CountChars','FPDFText_GetText','FPDFText_FindStart',
    'FPDFText_FindNext','FPDFText_FindClose','FPDFText_GetSchResultIndex','FPDFText_GetSchCount','FPDFText_GetRect','FPDFText_GetCharBox',
    'FPDFBookmark_GetFirstChild','FPDFBookmark_GetNextSibling','FPDFBookmark_GetTitle','FPDFBookmark_GetDest',
    'FPDFDest_GetDestPageIndex','FPDFLink_Enumerate','FPDFLink_GetAnnotRect','FPDFLink_GetDest','FPDFLink_GetAction',
    'FPDFAction_GetType','FPDFAction_GetDest','FPDFAction_GetURIPath','FPDFPage_GetAnnotCount','FPDFAnnot_IsSupportedSubtype','FPDFPage_CreateAnnot','FPDFPage_GetAnnot',
    'FPDFPage_CloseAnnot','FPDFAnnot_GetSubtype','FPDFAnnot_GetRect','FPDFAnnot_SetRect','FPDFAnnot_SetColor','FPDFAnnot_SetBorder','FPDFAnnot_SetFlags','FPDFAnnot_AddInkStroke','FPDFAnnot_AppendObject',
    'FPDFAnnot_GetFormFieldType',
    'FPDFAnnot_GetFormFieldName','FPDFAnnot_GetFormFieldValue','FPDFAnnot_GetFormFieldExportValue','FPDFAnnot_GetFormFieldFlags','FPDFAnnot_HasKey','FPDFAnnot_GetFormAdditionalActionJavaScript','FPDFAnnot_GetOptionCount',
    'FPDFAnnot_GetOptionLabel','FPDFAnnot_IsOptionSelected','FPDFAnnot_IsChecked','FPDFAnnot_SetStringValue','FPDFAnnot_GetStringValue',
    'FPDFPageObj_NewTextObj','FPDFText_SetText','FPDFPageObj_SetFillColor','FPDFPage_Flatten'
)

$manifestPath = Join-Path $PSScriptRoot '..\third_party\pdfium\154.0.8021\exports.manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([int]$manifest.requiredExportCount -ne $requiredExports.Count) {
    throw "PDFium manifest expects $($manifest.requiredExportCount) exports, but the verifier declares $($requiredExports.Count)."
}

function Read-U16([byte[]] $b, [int] $o) { [BitConverter]::ToUInt16($b, $o) }
function Read-U32([byte[]] $b, [int] $o) { [BitConverter]::ToUInt32($b, $o) }
function Convert-RvaToOffset([byte[]] $b, [uint32] $rva, [int] $sectionOffset, [int] $sectionCount, [int] $optionalOffset, [int] $optionalSize) {
    for ($i = 0; $i -lt $sectionCount; $i++) {
        $s = $sectionOffset + ($i * 40)
        $virtualSize = Read-U32 $b ($s + 8); $virtualAddress = Read-U32 $b ($s + 12)
        $rawSize = Read-U32 $b ($s + 16); $rawPointer = Read-U32 $b ($s + 20)
        $span = [Math]::Max($virtualSize, $rawSize)
        if ($rva -ge $virtualAddress -and $rva -lt ($virtualAddress + $span)) { return [int]($rawPointer + ($rva - $virtualAddress)) }
    }
    throw "RVA 0x{0:X8} is not mapped by a PE section." -f $rva
}
function Get-PeExports([string] $Path) {
    [byte[]] $b = [IO.File]::ReadAllBytes($Path)
    if ($b.Length -lt 0x40 -or $b[0] -ne 0x4D -or $b[1] -ne 0x5A) { throw "Not a DOS PE image: $Path" }
    $pe = [int](Read-U32 $b 0x3C)
    if ($pe -lt 0 -or $pe + 24 -ge $b.Length -or $b[$pe] -ne 0x50 -or $b[$pe+1] -ne 0x45) { throw "Invalid PE signature: $Path" }
    $machine = Read-U16 $b ($pe + 4); $sections = Read-U16 $b ($pe + 6)
    $optionalSize = Read-U16 $b ($pe + 20); $optional = $pe + 24
    $magic = Read-U16 $b $optional
    if ($magic -ne 0x20B -and $magic -ne 0x10B) { throw "Unsupported PE optional-header magic 0x{0:X}: $Path" -f $magic }
    $sectionOffset = $optional + $optionalSize
    $exportRva = Read-U32 $b ($optional + 112)
    if ($exportRva -eq 0) { throw "PE has no export directory: $Path" }
    $export = Convert-RvaToOffset $b $exportRva $sectionOffset $sections $optional $optionalSize
    $nameCount = Read-U32 $b ($export + 24); $namesRva = Read-U32 $b ($export + 32)
    $names = Convert-RvaToOffset $b $namesRva $sectionOffset $sections $optional $optionalSize
    $set = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($i = 0; $i -lt $nameCount; $i++) {
        $nameOffset = Convert-RvaToOffset $b (Read-U32 $b ($names + ($i * 4))) $sectionOffset $sections $optional $optionalSize
        $end = $nameOffset; while ($end -lt $b.Length -and $b[$end] -ne 0) { $end++ }
        [void]$set.Add([Text.Encoding]::ASCII.GetString($b, $nameOffset, $end - $nameOffset))
    }
    [pscustomobject]@{ Machine = $machine; Exports = $set }
}

foreach ($rid in $RuntimeIdentifiers) {
    if (-not $expected.ContainsKey($rid)) { throw "No pinned PDFium expectation exists for '$rid'." }
    $file = Join-Path $PackageRoot "runtimes\$rid\native\pdfium.dll"
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing PDFium asset: $file" }
    $item = Get-Item -LiteralPath $file
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToUpperInvariant()
    $e = $expected[$rid]
    if ($item.Length -ne $e.Length) { throw "$rid length mismatch: $($item.Length), expected $($e.Length)." }
    if ($hash -ne $e.Sha256) { throw "$rid SHA-256 mismatch: $hash, expected $($e.Sha256)." }
    $pe = Get-PeExports $file
    if ($pe.Machine -ne $e.Machine) { throw "$rid PE machine mismatch: 0x$('{0:X4}' -f $pe.Machine), expected 0x$('{0:X4}' -f $e.Machine)." }
    $missing = @($requiredExports | Where-Object { -not $pe.Exports.Contains($_) })
    if ($missing.Count) { throw "$rid is missing required PDFium exports: $($missing -join ', ')" }
    Write-Host ("PASS {0}: {1} bytes, SHA-256 {2}, PE machine 0x{3:X4}, {4} exports checked" -f $rid, $item.Length, $hash, $pe.Machine, $requiredExports.Count)
}
