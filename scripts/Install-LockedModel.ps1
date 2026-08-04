[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$ModelPrefix,
  [string]$Destination = "$env:LOCALAPPDATA\MLCCS\VideoSearch\models"
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$python = Join-Path $projectRoot 'worker/python/python.exe'
$manifest = Join-Path $projectRoot 'worker/manifests/models.lock.json'
if (-not (Test-Path $python)) { throw 'Private Python is missing.' }
$env:PYTHONPATH = Join-Path $projectRoot 'worker'
$status = "$Destination.download-status.json"
$cancel = "$Destination.download-cancel"
$stdout = "$Destination.download.stdout.log"
$stderr = "$Destination.download.stderr.log"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
Remove-Item -LiteralPath $stdout,$stderr,$cancel -Force -ErrorAction SilentlyContinue
$previousPreference = $ErrorActionPreference
try {
  $ErrorActionPreference = 'Continue'
  & $python -m mlccs_worker.model_download --manifest $manifest --destination $Destination `
    --status $status --cancel $cancel --prefix $ModelPrefix 1> $stdout 2> $stderr
  $exitCode = $LASTEXITCODE
} finally {
  $ErrorActionPreference = $previousPreference
}
if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout | Write-Output }
if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr | Write-Output }
if ($exitCode) { throw "Locked model installation failed with exit code $exitCode; see $status" }
$result = Get-Content -LiteralPath $status -Raw | ConvertFrom-Json
if ($result.status -ne 'Completed') { throw "Locked model installation did not complete: $($result.status)" }
Write-Host "VERIFIED $ModelPrefix -> $Destination"
