[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $projectRoot 'artifacts'
$logRoot = Join-Path $artifactRoot 'logs'
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
$log = Join-Path $logRoot ('build-windows-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))

Push-Location $projectRoot
try {
  dotnet restore MLCCS.VideoSearch.sln -p:RestoreLockedMode=false 2>&1 | Tee-Object -FilePath $log
  if ($LASTEXITCODE) { throw "Restore failed; see $log" }
  dotnet build MLCCS.VideoSearch.sln -c $Configuration -p:EnableWindowsTargeting=true --no-restore 2>&1 | Tee-Object -FilePath $log -Append
  if ($LASTEXITCODE) { throw "Build failed; see $log" }
  dotnet build tools/MLCCS.VideoSearch.SigningTool/MLCCS.VideoSearch.SigningTool.csproj -c $Configuration 2>&1 | Tee-Object -FilePath $log -Append
  if ($LASTEXITCODE) { throw "Signing tool build failed; see $log" }
  if (-not $SkipTests) {
    dotnet test tests/MLCCS.VideoSearch.Core.Tests/MLCCS.VideoSearch.Core.Tests.csproj -c $Configuration --no-build --logger "trx;LogFileName=core.trx" 2>&1 | Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE) { throw "Core tests failed; see $log" }
    & (Join-Path $projectRoot 'worker/python/python.exe') -m unittest discover -s tests/python -v 2>&1 | Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE) { throw "Worker tests failed; see $log" }
  }
} finally { Pop-Location }

