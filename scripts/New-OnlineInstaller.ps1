[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$FrozenRelease,
  [Parameter(Mandatory)][string]$RuntimeStage,
  [Parameter(Mandatory)][string]$VisualModelStage,
  [Parameter(Mandatory)][string]$TextModelStage,
  [string]$OcrModelStage,
  [string]$OutputRoot,
  [string]$PublicBaseUrl
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$release = Get-Content -LiteralPath (Join-Path $projectRoot 'release/version.json') -Raw | ConvertFrom-Json
$version = [string]$release.version
if ($version -ne '1.0.0') { throw "Expected v1.0.0 release source, got $version" }
if (-not $OutputRoot) { $OutputRoot = "R:\MLCCS-VideoSearch-Release-$version" }
if (-not $PublicBaseUrl) { $PublicBaseUrl = "https://lixinchen.ca/docs/mlccs-video-search/$version" }
$outputFull = [IO.Path]::GetFullPath($OutputRoot)
if ([IO.Path]::GetPathRoot($outputFull) -eq $outputFull) { throw "Refusing drive-root output: $outputFull" }
if (Test-Path -LiteralPath $outputFull) { Remove-Item -LiteralPath $outputFull -Recurse -Force }
$stageRoot = Join-Path $outputFull 'stage'
$packageRoot = Join-Path $outputFull 'packages'
$downloadRoot = Join-Path $outputFull 'downloads'
New-Item -ItemType Directory -Force -Path $stageRoot,$packageRoot,$downloadRoot | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
public sealed class ReleaseHash { public string Path { get; set; } = ""; public long Size { get; set; } public string Sha256 { get; set; } = ""; }
public static class ReleaseHasher {
  public static ReleaseHash[] Tree(string root) {
    var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var values = new ConcurrentBag<ReleaseHash>();
    Parallel.ForEach(Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories), path => {
      using (var stream = File.OpenRead(path)) {
        using (var algorithm = SHA256.Create()) {
          var digest = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
          values.Add(new ReleaseHash { Path=Path.GetFullPath(path).Substring(prefix.Length).Replace('\\','/'),
            Size=stream.Length, Sha256=digest });
        }
      }
    });
    return values.OrderBy(item=>item.Path,StringComparer.Ordinal).ToArray();
  }
}
'@

function Copy-Tree([string]$Source,[string]$Destination,[string[]]$Exclude = @()) {
  New-Item -ItemType Directory -Force -Path $Destination | Out-Null
  $arguments = @($Source,$Destination,'/E','/R:2','/W:1','/NFL','/NDL','/NJH','/NJS')
  if ($Exclude.Count) { $arguments += '/XD'; $arguments += $Exclude }
  & robocopy @arguments | Out-Null
  if ($LASTEXITCODE -gt 7) { throw "robocopy failed: $Source" }
}

function New-Component([string]$Id,[string]$Name,[string]$Description,[string]$Scope,
  [bool]$Required,[bool]$DefaultSelected,[string]$Stage) {
  if (-not (Test-Path -LiteralPath $Stage)) { throw "Missing component stage: $Stage" }
  $files = @([ReleaseHasher]::Tree($Stage) | ForEach-Object { [ordered]@{ path=$_.Path; size=$_.Size; sha256=$_.Sha256 } })
  if (-not $files.Count) { throw "Empty component stage: $Stage" }
  $archiveName = "$Id-$version.zip"
  $archivePath = Join-Path $packageRoot $archiveName
  & 7z a -tzip -mx=5 -mmt=on $archivePath (Join-Path $Stage '*') | Out-Host
  if ($LASTEXITCODE) { throw "7z failed for $Id" }
  $archive = Get-Item -LiteralPath $archivePath
  [ordered]@{
    id=$Id; name=$Name; description=$Description; url="$PublicBaseUrl/$archiveName"; installScope=$Scope
    required=$Required; defaultSelected=$DefaultSelected; size=$archive.Length
    sha256=(Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant(); files=$files
  }
}

foreach ($path in $FrozenRelease,$RuntimeStage,$VisualModelStage,$TextModelStage) {
  if (-not (Test-Path -LiteralPath $path)) { throw "Required input is missing: $path" }
}
if (-not (Test-Path -LiteralPath (Join-Path $FrozenRelease 'ui\MLCCS.VideoSearch.UI.exe'))) {
  throw 'Frozen release does not contain the UI executable.'
}

$coreStage = Join-Path $stageRoot 'app-core'
Copy-Tree $FrozenRelease $coreStage @((Join-Path $FrozenRelease 'worker\python'),(Join-Path $FrozenRelease 'worker\models'),(Join-Path $FrozenRelease '_development'))
$licenseStage = Join-Path $coreStage 'licenses'
python (Join-Path $projectRoot 'scripts/generate_release_metadata.py') --output $licenseStage
if ($LASTEXITCODE) { throw 'SBOM/license generation failed.' }
$runtimeComponent = Join-Path $stageRoot 'runtime'
$visualComponent = Join-Path $stageRoot 'visual-model'
$textComponent = Join-Path $stageRoot 'text-model'
Copy-Tree $RuntimeStage $runtimeComponent
Copy-Tree $VisualModelStage $visualComponent
Copy-Tree $TextModelStage $textComponent

$qdrantLock = Get-Content -LiteralPath (Join-Path $projectRoot 'release/qdrant.lock.json') -Raw | ConvertFrom-Json
$qdrantArchive = Join-Path $downloadRoot $qdrantLock.filename
Invoke-WebRequest -Uri $qdrantLock.url -OutFile $qdrantArchive
if ((Get-Item $qdrantArchive).Length -ne [long]$qdrantLock.size -or
    (Get-FileHash $qdrantArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $qdrantLock.sha256) {
  throw 'Qdrant Server lock verification failed.'
}
$qdrantStage = Join-Path $stageRoot 'qdrant'
Expand-Archive -LiteralPath $qdrantArchive -DestinationPath $qdrantStage
if ((Get-ChildItem $qdrantStage -Filter qdrant.exe -Recurse).Count -ne 1) { throw 'Qdrant component must contain exactly one qdrant.exe.' }

$components = @(
  (New-Component 'app-core' '应用核心' 'UI、Agent、Worker 与外置更新器' 'current' $true $true $coreStage),
  (New-Component 'private-runtime' '私有 CUDA 运行时' 'CPython 3.12 与 PyTorch 2.7.1+cu128' 'runtime' $true $true $runtimeComponent),
  (New-Component 'qdrant-server' 'Qdrant Server 1.18.3' '私有回环向量服务' 'qdrant' $true $true $qdrantStage),
  (New-Component 'visual-model' 'OpenCLIP Standard' '必装视觉模型' 'visual-model' $true $true $visualComponent),
  (New-Component 'text-model' 'BGE Small 中文' '必装文本语义模型' 'text-model' $true $true $textComponent)
)
if ($OcrModelStage) { $components += New-Component 'ocr-models' 'PP-OCRv5 中文模型' '可选 OCR 模型' 'ocr-models' $false $false $OcrModelStage }

$unsignedManifest = [ordered]@{
  schemaVersion=1; productVersion=$version; minimumCompatibleVersion='1.0.0'
  requirements=[ordered]@{ minimumWindowsVersion='Windows 10 1809'; minimumWindowsBuild=17763; architecture='x64'; gpuVendor='NVIDIA'; minimumVramBytes=4294967296; cudaRuntime='PyTorch 2.7.1+cu128'; cudaToolkitRequired=$false }
  entryPoint='current/ui/MLCCS.VideoSearch.UI.exe'; publishedUtc=[DateTimeOffset]::UtcNow.ToString('O')
  releaseNotes=Get-Content -LiteralPath (Join-Path $projectRoot 'release-notes.md') -Raw
  mandatory=$false; components=$components; keyId='manifest-v1'; manifestSignature=''
}
$manifestPath = Join-Path $packageRoot 'release-manifest.unsigned.json'
$unsignedManifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

$installerProject = Join-Path $projectRoot 'installer/MLCCS.VideoSearch.OnlineInstaller/MLCCS.VideoSearch.OnlineInstaller.csproj'
$setupStage = Join-Path $outputFull 'setup'
dotnet publish $installerProject -c Release -r win-x64 --self-contained true -p:VersionPrefix=$version `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -o $setupStage
if ($LASTEXITCODE) { throw 'Installer publish failed.' }
$setupSource = Join-Path $setupStage 'MLCCS-VideoSearch-Online-Setup.exe'
$setupTarget = Join-Path $packageRoot "MLCCS-VideoSearch-Online-Setup-$version.exe"
Copy-Item -LiteralPath $setupSource -Destination $setupTarget

$publication = [ordered]@{
  version=$version; generatedUtc=[DateTimeOffset]::UtcNow.ToString('O')
  setup=[ordered]@{ file=(Split-Path -Leaf $setupTarget); size=(Get-Item $setupTarget).Length; sha256=(Get-FileHash $setupTarget -Algorithm SHA256).Hash.ToLowerInvariant() }
  unsignedManifest=(Split-Path -Leaf $manifestPath)
  requiredDownloadBytes=($components | Where-Object required | Measure-Object size -Sum).Sum
  components=@($components | ForEach-Object { [ordered]@{ id=$_.id; file=([Uri]$_.url).Segments[-1]; size=$_.size; sha256=$_.sha256 } })
}
$publication | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputFull 'publication-record.json') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $licenseStage 'MLCCS-VideoSearch-1.0.0.cdx.json') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $licenseStage 'THIRD-PARTY-LICENSES.csv') -Destination $packageRoot
Write-Host "Unsigned v1.0.0 payload ready at $outputFull. Return only manifest metadata to the Mac for production signing."
