[CmdletBinding()]
param([string]$ArtifactManifest = 'worker/manifests/dependencies.lock.json')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $projectRoot $ArtifactManifest
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$downloadRoot = Join-Path $projectRoot 'artifacts/downloads'
$runtimeRoot = Join-Path $projectRoot 'worker/python'
New-Item -ItemType Directory -Force -Path $downloadRoot,$runtimeRoot | Out-Null

Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Receive-ResumableFile([string]$Uri, [string]$Destination) {
  $offset = if (Test-Path -LiteralPath $Destination) { (Get-Item -LiteralPath $Destination).Length } else { 0 }
  $handler = [System.Net.Http.HttpClientHandler]::new()
  $client = [System.Net.Http.HttpClient]::new($handler)
  $client.Timeout = [TimeSpan]::FromHours(2)
  $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Uri)
  if ($offset -gt 0) {
    $request.Headers.Range = [System.Net.Http.Headers.RangeHeaderValue]::new([long]$offset, $null)
  }
  try {
    $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    try {
      $response.EnsureSuccessStatusCode() | Out-Null
      $append = $offset -gt 0 -and $response.StatusCode -eq [System.Net.HttpStatusCode]::PartialContent
      $mode = if ($append) { [IO.FileMode]::Append } else { [IO.FileMode]::Create }
      $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
      try {
        $output = [IO.File]::Open($Destination, $mode, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $input.CopyTo($output) } finally { $output.Dispose() }
      } finally { $input.Dispose() }
    } finally { $response.Dispose() }
  } finally {
    $request.Dispose()
    $client.Dispose()
    $handler.Dispose()
  }
}

function Invoke-NativeCommand([string]$FilePath, [string[]]$Arguments, [string]$FailureMessage) {
  $stdout = Join-Path $downloadRoot 'native-command.stdout.log'
  $stderr = Join-Path $downloadRoot 'native-command.stderr.log'
  Remove-Item -LiteralPath $stdout,$stderr -Force -ErrorAction SilentlyContinue
  $previousPreference = $ErrorActionPreference
  try {
    # Windows PowerShell 5.1 turns any native stderr line into an ErrorRecord.
    # Capture both streams before applying the real process exit-code gate.
    $ErrorActionPreference = 'Continue'
    & $FilePath @Arguments 1> $stdout 2> $stderr
    $exitCode = $LASTEXITCODE
  } finally {
    $ErrorActionPreference = $previousPreference
  }
  if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout | Write-Output }
  if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr | Write-Output }
  if ($exitCode -ne 0) { throw "$FailureMessage (exit $exitCode)" }
}

function Get-VerifiedFile($artifact) {
  $destination = Join-Path $downloadRoot $artifact.filename
  if (Test-Path $destination) {
    if ((Get-Item $destination).Length -eq [int64]$artifact.size -and
        (Get-FileHash $destination -Algorithm SHA256).Hash.ToLowerInvariant() -eq $artifact.sha256) {
      return $destination
    }
    Remove-Item -LiteralPath $destination -Force
  }
  if ($artifact.url -like 'bundled:*') {
    $relativePath = $artifact.url.Substring('bundled:'.Length).Replace('/', [IO.Path]::DirectorySeparatorChar)
    $source = Join-Path $projectRoot $relativePath
    if (-not (Test-Path -LiteralPath $source)) { throw "Bundled artifact is missing: $($artifact.id)" }
    if ((Get-Item $source).Length -ne [int64]$artifact.size) { throw "Bundled size mismatch: $($artifact.id)" }
    if ((Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $artifact.sha256) { throw "Bundled hash mismatch: $($artifact.id)" }
    Copy-Item -LiteralPath $source -Destination $destination
    return $destination
  }
  $partial = "$destination.partial"
  Receive-ResumableFile ([string]$artifact.url) $partial
  if ((Get-Item $partial).Length -ne [int64]$artifact.size) { throw "Size mismatch: $($artifact.id)" }
  if ((Get-FileHash $partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $artifact.sha256) { throw "Hash mismatch: $($artifact.id)" }
  Move-Item -Force $partial $destination
  return $destination
}

$pythonArtifact = $manifest.artifacts | Where-Object kind -eq 'python-runtime'
if (-not $pythonArtifact) { throw 'python-runtime is absent from dependencies.lock.json' }
$pythonZip = Get-VerifiedFile $pythonArtifact
Expand-Archive -Force $pythonZip $runtimeRoot
$pth = Get-ChildItem $runtimeRoot -Filter 'python*._pth' | Select-Object -First 1
(Get-Content $pth.FullName) -replace '^#import site$', 'import site' | Set-Content -Encoding ascii $pth.FullName

$getPip = Get-VerifiedFile ($manifest.artifacts | Where-Object kind -eq 'get-pip')
Invoke-NativeCommand (Join-Path $runtimeRoot 'python.exe') @($getPip,'--disable-pip-version-check') 'Private runtime pip bootstrap failed.'
foreach ($wheel in $manifest.artifacts | Where-Object kind -eq 'python-wheel') { Get-VerifiedFile $wheel | Out-Null }
Invoke-NativeCommand (Join-Path $runtimeRoot 'python.exe') @('-m','pip','install','--no-index','--find-links',$downloadRoot,'--require-hashes','-r',(Join-Path $projectRoot 'worker/requirements.hashed.txt')) 'Private runtime dependency installation failed.'
