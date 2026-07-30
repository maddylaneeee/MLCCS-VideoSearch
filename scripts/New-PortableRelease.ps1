[CmdletBinding()]
param(
  [string]$Version = '0.1.0',
  [switch]$AllowMissingPrivateRuntime,
  [string]$PreinstalledModelsRoot,
  [switch]$IncludeSourceSnapshot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $projectRoot "artifacts/portable/$Version/MLCCS-VideoSearch"
$zip = Join-Path $projectRoot "artifacts/portable/MLCCS-VideoSearch-$Version-win-x64.zip"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

foreach ($project in 'UI','Agent','Updater') {
  $projectPath = Join-Path $projectRoot "src/MLCCS.VideoSearch.$project/MLCCS.VideoSearch.$project.csproj"
  $output = Join-Path $stage $project.ToLowerInvariant()
  dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:EnableWindowsTargeting=true -o $output
  if ($LASTEXITCODE) { throw "Publish failed: $project" }
  # WinAppSDK unpackaged publish currently omits the application's PRI/XBF payload.
  # Copy the generated resources explicitly so ms-appx page navigation works on a clean machine.
  if ($project -eq 'UI') {
    $uiBuild = Join-Path $projectRoot 'src/MLCCS.VideoSearch.UI/bin/Release/net10.0-windows10.0.17763.0/win-x64'
    foreach ($resource in 'MLCCS.VideoSearch.UI.pri','MainWindow.xbf') {
      $resourcePath = Join-Path $uiBuild $resource
      if (-not (Test-Path $resourcePath)) { throw "Published UI resource is missing: $resourcePath" }
      Copy-Item -Force $resourcePath $output
    }
    Copy-Item -Recurse -Force (Join-Path $uiBuild 'Pages') (Join-Path $output 'Pages')
  }
}
Copy-Item -Recurse (Join-Path $projectRoot 'worker/mlccs_worker') (Join-Path $stage 'worker/mlccs_worker')
Copy-Item (Join-Path $projectRoot 'worker/pyproject.toml') (Join-Path $stage 'worker/pyproject.toml')
Copy-Item -Recurse (Join-Path $projectRoot 'worker/manifests') (Join-Path $stage 'worker/manifests')
if (Test-Path (Join-Path $projectRoot 'worker/python/python.exe')) {
  $pythonSource = Join-Path $projectRoot 'worker/python'
  $pythonTarget = Join-Path $stage 'worker/python'
  New-Item -ItemType Directory -Force $pythonTarget | Out-Null
  & robocopy.exe $pythonSource $pythonTarget /E /COPY:DAT /DCOPY:DA /R:2 /W:1 /MT:16 /NFL /NDL /NJH /NJS /NP
  if ($LASTEXITCODE -gt 7) { throw "Private Python copy failed with robocopy exit code $LASTEXITCODE" }
}
elseif (-not $AllowMissingPrivateRuntime) { throw 'Private Python runtime is missing. Run Build-PrivateRuntime.ps1.' }
$installedModels = if ($PreinstalledModelsRoot) {
  [System.IO.Path]::GetFullPath($PreinstalledModelsRoot)
} else {
  Join-Path $env:LOCALAPPDATA 'MLCCS/VideoSearch/models'
}
$modelManifest = Get-Content -Raw (Join-Path $projectRoot 'worker/manifests/models.lock.json') | ConvertFrom-Json
$standardModel = @($modelManifest.artifacts | Where-Object { $_.id -like 'openclip-standard-*' })
foreach ($artifact in $standardModel) {
  $source = Join-Path $installedModels $artifact.installPath
  if (-not (Test-Path $source)) {
    if ($AllowMissingPrivateRuntime) { continue }
    throw "Locked standard model is missing: $source"
  }
  if ((Get-Item $source).Length -ne $artifact.size -or (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $artifact.sha256) {
    throw "Locked standard model verification failed: $source"
  }
  $target = Join-Path (Join-Path $stage 'worker/models') $artifact.installPath
  New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
  Copy-Item -Force $source $target
}
Copy-Item -Recurse (Join-Path $projectRoot 'schemas') (Join-Path $stage 'schemas')
Copy-Item (Join-Path $projectRoot 'MODELS_AND_LICENSES.md') $stage
Copy-Item (Join-Path $projectRoot 'PORTABLE_RELEASE.md') $stage
Copy-Item (Join-Path $projectRoot 'ITERATION_HANDOFF.md') $stage
Copy-Item (Join-Path $projectRoot 'FEEDBACK.md') $stage
Copy-Item (Join-Path $projectRoot 'Start-MLCCS-VideoSearch.cmd') $stage
if ($IncludeSourceSnapshot) {
  $development = Join-Path $stage '_development'
  New-Item -ItemType Directory -Force $development | Out-Null
  $sourceZip = Join-Path $development 'source-current.zip'
  $sourceArchiver = Get-Command 7z.exe -ErrorAction SilentlyContinue
  if (-not $sourceArchiver) { throw '7-Zip is required when IncludeSourceSnapshot is used.' }
  Push-Location $projectRoot
  try {
    & $sourceArchiver.Source a -tzip $sourceZip '*' -mx=5 -y `
      '-xr!artifacts' '-xr!worker\python' '-xr!worker\models' `
      '-xr!bin' '-xr!obj' '-xr!.git' '-xr!.vs'
    if ($LASTEXITCODE) { throw 'Source snapshot creation failed.' }
  } finally { Pop-Location }
  Copy-Item (Join-Path $projectRoot 'ITERATION_HANDOFF.md') $development
  Copy-Item (Join-Path $projectRoot 'FEEDBACK.md') $development
}
Set-Content -Encoding utf8 (Join-Path $stage 'version.json') (ConvertTo-Json @{ version=$Version; protocol='1.0'; architecture='x64' })
if (Test-Path $zip) { Remove-Item -Force $zip }
$sevenZip = Get-Command 7z.exe -ErrorAction SilentlyContinue
if ($sevenZip) {
  Push-Location $stage
  try {
    & $sevenZip.Source a -tzip $zip '*' -mx=5 -y
    if ($LASTEXITCODE) { throw '7-Zip portable archive creation failed.' }
  } finally { Pop-Location }
} else {
  Compress-Archive -CompressionLevel Optimal -Path (Join-Path $stage '*') -DestinationPath $zip
}
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Encoding utf8 "$zip.sha256" "$hash  $(Split-Path -Leaf $zip)"
Write-Output $zip
