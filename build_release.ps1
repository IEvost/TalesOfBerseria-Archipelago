# Builds a release of the Tales of Berseria Remastered Archipelago integration.
#   1. Regenerates data/game_data.json from the game files (needs the game installed)
#   2. Packs apworld/tales_of_berseria into tales_of_berseria.apworld
#   3. Builds the BepInEx plugin
#   4. Writes release/BerseriaAP-<version>.zip
param(
    [string]$GameDir = "D:\Games\Tales of Berseria Remastered",
    [switch]$SkipDataGen
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$version = (Get-Content "$root\apworld\tales_of_berseria\archipelago.json" | ConvertFrom-Json).world_version
$out = Join-Path $root "release"
$stage = Join-Path $out "stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

if (-not $SkipDataGen) {
    Write-Host "== Generating game data"
    dotnet run -c Release --project "$root\src\BerseriaAP.DataTool" -- gen `
        "$GameDir\Tales of Berseria Remastered_Game_Data\StreamingAssets" "$root\data\raw" "$root\data\game_data.json"
    if ($LASTEXITCODE) { throw "data generation failed" }
}
Copy-Item "$root\data\game_data.json" "$root\apworld\tales_of_berseria\game_data.json" -Force

Write-Host "== Packing apworld"
$apworld = Join-Path $stage "tales_of_berseria.apworld"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$tmp = Join-Path $stage "apw"
Copy-Item "$root\apworld\tales_of_berseria" "$tmp\tales_of_berseria" -Recurse
Get-ChildItem $tmp -Recurse -Directory -Filter "__pycache__" | Remove-Item -Recurse -Force
# Packed apworlds need the container fields Archipelago's own builder adds (required from AP 0.7.0).
$manifestPath = "$tmp\tales_of_berseria\archipelago.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifest | Add-Member -NotePropertyName version -NotePropertyValue 7 -Force
$manifest | Add-Member -NotePropertyName compatible_version -NotePropertyValue 7 -Force
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Compress))
[System.IO.Compression.ZipFile]::CreateFromDirectory($tmp, $apworld)
Remove-Item $tmp -Recurse -Force

Write-Host "== Building plugin"
dotnet build "$root\src\BerseriaAP.Plugin" -c Release -p:GameDir="$GameDir" -t:Build -p:SkipDeploy=true | Out-Host
if ($LASTEXITCODE) { throw "plugin build failed" }
$pluginOut = "$root\src\BerseriaAP.Plugin\bin\Release\net6.0"
$pdir = New-Item -ItemType Directory -Force (Join-Path $stage "BepInEx\plugins\BerseriaAP")
foreach ($f in "BerseriaAP.dll", "Archipelago.MultiClient.Net.dll", "Newtonsoft.Json.dll") { Copy-Item "$pluginOut\$f" $pdir }

Copy-Item "$root\README.md" $stage -ErrorAction SilentlyContinue
Copy-Item "$root\LICENSE" $stage
Copy-Item "$root\apworld\tales_of_berseria\docs\setup_en.md" "$stage\SETUP.md"

# Zip entries must use '/' separators. Windows PowerShell 5.1 (.NET Framework) writes '\', which some
# unzip tools turn into literal file names, so require PowerShell 7+.
if ($PSVersionTable.PSVersion.Major -lt 7) { throw "Run this script with PowerShell 7+ (pwsh)." }
$zip = Join-Path $out "BerseriaAP-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Copy-Item $apworld (Join-Path $out "tales_of_berseria.apworld") -Force
Write-Host "== Done: $zip"
