param(
    [string]$SourceImage = "$PSScriptRoot\..\Assets\Brand\elliepdf-logo-master.png",
    [string]$OutputIco = "$PSScriptRoot\..\Assets\AppIcon.ico"
)

python "$PSScriptRoot\generate_brand_assets.py"
