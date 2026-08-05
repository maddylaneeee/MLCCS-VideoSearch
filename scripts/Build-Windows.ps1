[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$release = Get-Content -LiteralPath (Join-Path $projectRoot 'release/version.json') -Raw | ConvertFrom-Json
$version = [string]$release.version
if ($version -ne '1.0.0') { throw "release/version.json is not the v1.0.0 release source: $version" }
$artifactRoot = Join-Path $projectRoot 'artifacts'
$logRoot = Join-Path $artifactRoot 'logs'
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
$log = Join-Path $logRoot ('build-windows-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))

Push-Location $projectRoot
try {
  python scripts/validate_repository.py 2>&1 | Tee-Object -FilePath $log
  if ($LASTEXITCODE) { throw "Repository validation failed; see $log" }
  dotnet restore MLCCS.VideoSearch.sln -p:RestoreLockedMode=false -p:VersionPrefix=$version 2>&1 | Tee-Object -FilePath $log -Append
  if ($LASTEXITCODE) { throw "Restore failed; see $log" }
  python scripts/generate_release_metadata.py --output (Join-Path $artifactRoot 'release-metadata') 2>&1 | Tee-Object -FilePath $log -Append
  if ($LASTEXITCODE) { throw "SBOM/license generation failed; see $log" }
  dotnet build MLCCS.VideoSearch.sln -c $Configuration -p:EnableWindowsTargeting=true -p:VersionPrefix=$version --no-restore 2>&1 | Tee-Object -FilePath $log -Append
  if ($LASTEXITCODE) { throw "Build failed; see $log" }
  dotnet build tools/MLCCS.VideoSearch.SigningTool/MLCCS.VideoSearch.SigningTool.csproj -c $Configuration 2>&1 | Tee-Object -FilePath $log -Append
  if ($LASTEXITCODE) { throw "Signing tool build failed; see $log" }
  dotnet build installer/MLCCS.VideoSearch.OnlineInstaller/MLCCS.VideoSearch.OnlineInstaller.csproj -c $Configuration -p:VersionPrefix=$version 2>&1 | Tee-Object -FilePath $log -Append
  if ($LASTEXITCODE) { throw "Installer build failed; see $log" }
  dotnet build tests/MLCCS.VideoSearch.Installer.Acceptance/MLCCS.VideoSearch.Installer.Acceptance.csproj -c $Configuration -p:VersionPrefix=$version 2>&1 | Tee-Object -FilePath $log -Append
  if ($LASTEXITCODE) { throw "Installer acceptance build failed; see $log" }
  if (-not $SkipTests) {
    dotnet test tests/MLCCS.VideoSearch.Core.Tests/MLCCS.VideoSearch.Core.Tests.csproj -c $Configuration --no-build --logger "trx;LogFileName=core.trx" 2>&1 | Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE) { throw "Core tests failed; see $log" }
    $privatePython = Join-Path $projectRoot 'worker/python/python.exe'
    $pythonExe = if (Test-Path -LiteralPath $privatePython) { $privatePython } else { (Get-Command python -ErrorAction Stop).Source }
    $pythonStdout = Join-Path $logRoot 'python-tests.stdout.log'
    $pythonStderr = Join-Path $logRoot 'python-tests.stderr.log'
    Remove-Item -LiteralPath $pythonStdout, $pythonStderr -Force -ErrorAction SilentlyContinue
    # unittest intentionally writes its progress to stderr. Redirect both streams at
    # process level so Windows PowerShell 5.1/WinRM cannot turn success output into a
    # terminating NativeCommandError.
    $pythonProcess = Start-Process -FilePath $pythonExe -ArgumentList @('-m','unittest','discover','-s','tests/python','-v') -NoNewWindow -Wait -PassThru -RedirectStandardOutput $pythonStdout -RedirectStandardError $pythonStderr
    if (Test-Path -LiteralPath $pythonStdout) { Get-Content -LiteralPath $pythonStdout | Tee-Object -FilePath $log -Append }
    if (Test-Path -LiteralPath $pythonStderr) { Get-Content -LiteralPath $pythonStderr | Tee-Object -FilePath $log -Append }
    if ($pythonProcess.ExitCode) { throw "Worker tests failed; see $log" }
  }
} finally { Pop-Location }
