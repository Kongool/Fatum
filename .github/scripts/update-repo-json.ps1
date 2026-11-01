# Updates repo.json for a release. release.yml runs it for real; ci.yml runs it against a
# copy so a break here shows up on an ordinary push rather than mid-release.
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ManifestPath,
    [int]$DownloadCount = 0,
    [string]$Path = 'repo.json'
)

$ErrorActionPreference = 'Stop'

$entry = @(Get-Content -Raw -Path $Path | ConvertFrom-Json)[0]
$manifest = Get-Content -Raw -Path $ManifestPath | ConvertFrom-Json

$entry.AssemblyVersion = $Version
$entry.TestingAssemblyVersion = $Version
$entry.DownloadCount = $DownloadCount
$entry.LastUpdate = [string][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

# The built manifest is the source for these, so an SDK bump (new API level) or a
# reworded description reaches the plugin repo without a second edit.
foreach ($field in 'DalamudApiLevel', 'Description', 'Punchline') {
    $entry.$field = $manifest.$field
}

# -AsArray keeps the [ ... ] wrapper Dalamud requires around the single entry.
ConvertTo-Json -InputObject $entry -Depth 10 -AsArray | Set-Content -Path $Path -Encoding utf8NoBOM
