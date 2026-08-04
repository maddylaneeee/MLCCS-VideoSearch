[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$ArtifactRoot,
  [string]$PythonExe
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactFull = [IO.Path]::GetFullPath($ArtifactRoot)
if ([IO.Path]::GetPathRoot($artifactFull) -eq $artifactFull) {
  throw "Refusing drive-root artifact directory: $artifactFull"
}
New-Item -ItemType Directory -Force -Path $artifactFull | Out-Null
$logRoot = Join-Path $artifactFull 'logs'
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
$statusPath = Join-Path $artifactFull 'status.json'
$started = [DateTimeOffset]::UtcNow
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'

function Write-Status([string]$State,[string]$Step,[string]$ErrorMessage = '') {
  $value = [ordered]@{
    state=$State; step=$Step; startedUtc=$started.ToString('O')
    updatedUtc=[DateTimeOffset]::UtcNow.ToString('O'); error=$ErrorMessage
  }
  [IO.File]::WriteAllText($statusPath, ($value | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}

function Invoke-Native([string]$Name,[string]$FilePath,[string[]]$Arguments) {
  Write-Status 'running' $Name
  $stdout = Join-Path $logRoot "$Name.stdout.log"
  $stderr = Join-Path $logRoot "$Name.stderr.log"
  Remove-Item -LiteralPath $stdout,$stderr -Force -ErrorAction SilentlyContinue
  $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -WorkingDirectory $projectRoot `
    -NoNewWindow -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
  if ($process.ExitCode) {
    $tail = if (Test-Path $stderr) { @(Get-Content -LiteralPath $stderr -Tail 20) -join "`n" } else { '' }
    throw "$Name failed with exit code $($process.ExitCode).`n$tail"
  }
}

Write-Status 'running' 'start'
try {
  $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
  Invoke-Native 'restore' $dotnet @('restore','MLCCS.VideoSearch.sln','-p:RestoreLockedMode=false','-p:VersionPrefix=1.0.0')
  Invoke-Native 'windows-build' $dotnet @('build','MLCCS.VideoSearch.sln','-c','Release','-p:EnableWindowsTargeting=true','-p:VersionPrefix=1.0.0','-p:UseSharedCompilation=false','--no-restore')
  Invoke-Native 'core-tests' $dotnet @('test','tests/MLCCS.VideoSearch.Core.Tests/MLCCS.VideoSearch.Core.Tests.csproj','-c','Release','--no-build')
  Invoke-Native 'installer-acceptance-build' $dotnet @('build','tests/MLCCS.VideoSearch.Installer.Acceptance/MLCCS.VideoSearch.Installer.Acceptance.csproj','-c','Release','-p:EnableWindowsTargeting=true','-p:UseSharedCompilation=false')
  Invoke-Native 'installer-acceptance' $dotnet @('run','--project','tests/MLCCS.VideoSearch.Installer.Acceptance/MLCCS.VideoSearch.Installer.Acceptance.csproj','-c','Release','--no-build')

  if ($PythonExe) {
    $pythonFull = [IO.Path]::GetFullPath($PythonExe)
    if (-not (Test-Path -LiteralPath $pythonFull)) { throw "Python runtime not found: $pythonFull" }
    $oldPythonPath = $env:PYTHONPATH
    try {
      $env:PYTHONPATH = Join-Path $projectRoot 'worker'
      Invoke-Native 'python-tests' $pythonFull @('-m','unittest','discover','-s',(Join-Path $projectRoot 'tests/python'),'-v')
    } finally { $env:PYTHONPATH = $oldPythonPath }
  }

  $setupRoot = Join-Path $artifactFull 'setup'
  Invoke-Native 'publish-installer' $dotnet @(
    'publish','installer/MLCCS.VideoSearch.OnlineInstaller/MLCCS.VideoSearch.OnlineInstaller.csproj',
    '-c','Release','-r','win-x64','--self-contained','true','-p:VersionPrefix=1.0.0',
    '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=false','-p:UseSharedCompilation=false','-o',$setupRoot)
  $setup = Join-Path $setupRoot 'MLCCS-VideoSearch-Online-Setup.exe'
  if (-not (Test-Path -LiteralPath $setup)) { throw "Published setup is missing: $setup" }

  $report = Join-Path $artifactFull 'bare-windows-prerequisite-report.json'
  $oldPath = $env:PATH
  try {
    $env:PATH = ''
    Invoke-Native 'bare-path-launch' $setup @("--prerequisite-report=$report")
  } finally { $env:PATH = $oldPath }
  $reportValue = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
  if (-not $reportValue.CanInstall) { throw 'Published setup rejected the supported Windows acceptance host.' }

  $setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
  [IO.File]::WriteAllText((Join-Path $artifactFull 'result.json'), ([ordered]@{
      commit=(git -C $projectRoot rev-parse HEAD); setup=$setup; setupSha256=$setupHash
      prerequisiteReport=$report; pythonTests=([bool]$PythonExe); completedUtc=[DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
  Write-Status 'passed' 'complete'
} catch {
  Write-Status 'failed' 'exception' $_.Exception.Message
  throw
}
