[CmdletBinding()]
param([string]$Output = 'artifacts/handoff/MLCCS-VideoSearch-FromScratch-0.1.0-source.zip')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $projectRoot $Output
$stage = Join-Path $projectRoot 'artifacts/handoff/stage'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$copyArguments = @(
  $projectRoot, $stage, '/E', '/COPY:DAT', '/DCOPY:DA', '/R:2', '/W:1', '/MT:16',
  '/NFL', '/NDL', '/NJH', '/NJS', '/NP',
  '/XD', '.git', 'artifacts', 'bin', 'obj', '.vs', '.venv', '__pycache__', '.pytest_cache',
  (Join-Path $projectRoot 'worker/python'), (Join-Path $projectRoot 'release/private'), (Join-Path $projectRoot 'release/downloads'),
  '/XF', '*.pfx', '*.pem', '*.key'
)
& robocopy.exe @copyArguments
if ($LASTEXITCODE -gt 7) { throw "Source snapshot copy failed with robocopy exit code $LASTEXITCODE" }
Get-ChildItem -Recurse -Force $stage | Where-Object { $_.Name -match '\.(pfx|pem|key)$' -or $_.FullName -match '[\\/](models|cache|media)[\\/]' } | ForEach-Object { throw "Forbidden handoff content: $($_.FullName)" }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
if (Test-Path $destination) { Remove-Item -Force $destination }
$sevenZip = Get-Command 7z.exe -ErrorAction SilentlyContinue
if ($sevenZip) {
  Push-Location $stage
  try {
    & $sevenZip.Source a -tzip $destination '*' -mx=7 -y
    if ($LASTEXITCODE) { throw '7-Zip source archive creation failed.' }
  } finally { Pop-Location }
} else {
  Compress-Archive -CompressionLevel Optimal -Path (Join-Path $stage '*') -DestinationPath $destination
}
Write-Output $destination
