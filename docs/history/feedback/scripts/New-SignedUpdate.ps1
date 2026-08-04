[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Archive,
  [Parameter(Mandatory)][string]$ExternalPrivateKey,
  [Parameter(Mandatory)][string]$Version,
  [string]$MinimumVersion = '0.1.0',
  [Parameter(Mandatory)][ValidatePattern('^https://')][string]$DownloadUrl,
  [string]$ChannelDirectory = 'artifacts/update/stable',
  [switch]$Mandatory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
if ($ExternalPrivateKey.StartsWith($projectRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Private signing key must remain outside the source and handoff tree.' }
$versionDir = Join-Path $projectRoot "$ChannelDirectory/$Version"
New-Item -ItemType Directory -Force -Path $versionDir | Out-Null
$versionArchive = Join-Path $versionDir (Split-Path -Leaf $Archive)
Copy-Item -Force $Archive $versionArchive
$manifest = Join-Path $versionDir 'manifest.json'
dotnet run --project (Join-Path $projectRoot 'tools/MLCCS.VideoSearch.SigningTool') -- $versionArchive $ExternalPrivateKey $Version $MinimumVersion $DownloadUrl (Get-Date).ToUniversalTime().ToString('O') (Join-Path $projectRoot 'release-notes.md') $manifest $Mandatory.IsPresent.ToString().ToLowerInvariant()
if ($LASTEXITCODE) { throw 'Signing failed.' }
Write-Host "Versioned artifacts ready in $versionDir. Verify/upload them before atomically publishing latest.json."

