param(
    [string]$FrozenRelease = 'C:\Users\mattl\Downloads\1213',
    [string]$OutputRoot = 'R:\MLCCS-VideoSearch-Installer-0.3.0-feedback5',
    [string]$Version = '0.3.0-feedback5',
    [string]$PublicBaseUrl = 'https://lixinchen.ca/docs/mlccs-video-search/0.3.0-feedback5',
    [string]$ValidatedRuntimeStage,
    [switch]$Resume
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

public sealed class ManifestHash
{
    public string FullPath { get; init; } = "";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
}

public static class ManifestHasher
{
    public static ManifestHash[] HashTree(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
        var results = new ConcurrentBag<ManifestHash>();
        Parallel.ForEach(files, new ParallelOptions {
            MaxDegreeOfParallelism = Math.Max(2, Math.Min(12, Environment.ProcessorCount))
        }, path => {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.SequentialScan);
            results.Add(new ManifestHash {
                FullPath = path,
                Size = stream.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()
            });
        });
        return results.OrderBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
'@

function Invoke-RobocopyChecked {
    param([string[]]$Arguments)
    & robocopy @Arguments
    if ($LASTEXITCODE -gt 7) {
        throw "robocopy failed with exit code $LASTEXITCODE"
    }
}

function New-CleanDirectory {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) {
        $resolved = [System.IO.Path]::GetFullPath($Path)
        $allowedRoot = [System.IO.Path]::GetFullPath($OutputRoot)
        if (-not $resolved.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clear path outside output root: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Get-FileManifest {
    param([string]$Root)
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    @([ManifestHasher]::HashTree($Root) | ForEach-Object {
        [ordered]@{
            path = $_.FullPath.Substring($rootFull.Length).Replace('\', '/')
            size = $_.Size
            sha256 = $_.Sha256
        }
    })
}

function New-ComponentArchive {
    param(
        [string]$Id,
        [string]$Name,
        [string]$Description,
        [bool]$Required,
        [bool]$DefaultSelected,
        [string]$StagePath,
        [string]$Destination = 'install'
    )
    $archiveName = "$Id-$Version.zip"
    $archivePath = Join-Path $packages $archiveName
    if ($Resume -and (Test-Path -LiteralPath $archivePath)) {
        & 7z t $archivePath | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Existing archive verification failed for $Id with exit code $LASTEXITCODE"
        }
    } else {
        & 7z a -tzip -mx=5 -mmt=on $archivePath (Join-Path $StagePath '*') | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "7z failed for $Id with exit code $LASTEXITCODE"
        }
    }
    $archive = Get-Item -LiteralPath $archivePath
    [ordered]@{
        id = $Id
        name = $Name
        description = $Description
        required = $Required
        defaultSelected = $DefaultSelected
        archive = $archiveName
        url = "$PublicBaseUrl/$archiveName"
        size = $archive.Length
        sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        destination = $Destination
        files = @(Get-FileManifest -Root $StagePath)
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $FrozenRelease 'ui\MLCCS.VideoSearch.UI.exe'))) {
    throw "Frozen release is missing its UI executable: $FrozenRelease"
}
$stage = Join-Path $OutputRoot 'stage'
$packages = Join-Path $OutputRoot 'packages'
if ($Resume) {
    if (-not (Test-Path -LiteralPath $stage) -or -not (Test-Path -LiteralPath $packages)) {
        throw "Cannot resume because stage or packages directory is missing: $OutputRoot"
    }
} else {
    New-CleanDirectory -Path $OutputRoot
    New-Item -ItemType Directory -Path $stage, $packages -Force | Out-Null
}

$coreStage = Join-Path $stage 'app-core'
$runtimeStage = if ($ValidatedRuntimeStage) {
    [System.IO.Path]::GetFullPath($ValidatedRuntimeStage)
} else {
    Join-Path $stage 'runtime'
}
$visualStage = Join-Path $stage 'visual-model'
$ocrStage = Join-Path $stage 'ocr-models'
if (-not $Resume) {
New-Item -ItemType Directory -Path $coreStage, $visualStage, $ocrStage -Force | Out-Null
if (-not $ValidatedRuntimeStage) {
    New-Item -ItemType Directory -Path $runtimeStage -Force | Out-Null
}

Invoke-RobocopyChecked @(
    $FrozenRelease, $coreStage, '/E',
    '/XD',
    (Join-Path $FrozenRelease 'worker\python'),
    (Join-Path $FrozenRelease 'worker\models'),
    (Join-Path $FrozenRelease '_development'),
    '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS'
)

$runtimeTarget = Join-Path $runtimeStage 'worker\python'
if ($ValidatedRuntimeStage) {
    $validationMarker = "$runtimeStage.validated.json"
    if (-not (Test-Path -LiteralPath $runtimeTarget) -or
        -not (Test-Path -LiteralPath $validationMarker)) {
        throw "Validated runtime stage or marker is missing: $runtimeStage"
    }
} else {
    New-Item -ItemType Directory -Path $runtimeTarget -Force | Out-Null
    Invoke-RobocopyChecked @(
        (Join-Path $FrozenRelease 'worker\python'), $runtimeTarget, '/E',
        '/XF', '*.pyc', '*.pyo', '*.lib',
        '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS'
    )
}

$visualTarget = Join-Path $visualStage 'worker\models'
New-Item -ItemType Directory -Path $visualTarget -Force | Out-Null
Invoke-RobocopyChecked @(
    (Join-Path $FrozenRelease 'worker\models'), $visualTarget, '/E',
    '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS'
)

$localModels = Join-Path $env:LOCALAPPDATA 'MLCCS\VideoSearch\models'
if (Test-Path -LiteralPath $localModels) {
    foreach ($ocrModel in 'ppocrv5-mobile-det', 'ppocrv5-mobile-rec') {
        $ocrSource = Join-Path $localModels $ocrModel
        if (Test-Path -LiteralPath $ocrSource) {
            Invoke-RobocopyChecked @(
                $ocrSource, (Join-Path $ocrStage $ocrModel), '/E',
                '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS'
            )
        }
    }
}

if (-not $ValidatedRuntimeStage) {
    Write-Host 'Validating pruned private runtime imports...'
    & (Join-Path $runtimeTarget 'python.exe') -c @'
import av
import cv2
import ctranslate2
import faster_whisper
import open_clip
import paddle
import paddleocr
import scipy
import torch
import transformers
print("runtime-imports-ok", torch.__version__, torch.cuda.is_available())
'@
    if ($LASTEXITCODE -ne 0) {
        throw "Pruned runtime validation failed with exit code $LASTEXITCODE"
    }
    [ordered]@{
        validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        python = (Get-Item -LiteralPath (Join-Path $runtimeTarget 'python.exe')).VersionInfo.FileVersion
    } | ConvertTo-Json | Set-Content -LiteralPath "$runtimeStage.validated.json" -Encoding utf8NoBOM
}
} else {
    foreach ($requiredStage in $coreStage, $runtimeStage, $visualStage, $ocrStage) {
        if (-not (Test-Path -LiteralPath $requiredStage)) {
            throw "Cannot resume because a component stage is missing: $requiredStage"
        }
    }
}

$components = @()
$components += New-ComponentArchive `
    -Id 'app-core' `
    -Name '应用核心' `
    -Description '界面、后台服务、媒体与索引程序' `
    -Required $true `
    -DefaultSelected $true `
    -StagePath $coreStage
$components += New-ComponentArchive `
    -Id 'private-runtime' `
    -Name '私有 AI 运行环境' `
    -Description '视觉、语音和 OCR 的本地运行库，已移除开发文件' `
    -Required $true `
    -DefaultSelected $true `
    -StagePath $runtimeStage
$components += New-ComponentArchive `
    -Id 'visual-model' `
    -Name '基础视觉模型' `
    -Description '首次启动即可进行视频画面索引与语义搜索' `
    -Required $true `
    -DefaultSelected $true `
    -StagePath $visualStage

if ((Get-ChildItem -LiteralPath $ocrStage -File -Recurse -ErrorAction SilentlyContinue).Count -gt 0) {
    $components += New-ComponentArchive `
        -Id 'ocr-models' `
        -Name 'OCR 中文模型预下载' `
        -Description '可跳过；首次启用 OCR 时仍可按需下载' `
        -Required $false `
        -DefaultSelected $false `
        -StagePath $ocrStage `
        -Destination 'localModels'
}

$manifest = [ordered]@{
    version = $Version
    entryPoint = 'ui/MLCCS.VideoSearch.UI.exe'
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    components = $components
}
$manifestPath = Join-Path $packages 'installer-manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

$project = Join-Path $PSScriptRoot '..\installer\MLCCS.VideoSearch.OnlineInstaller\MLCCS.VideoSearch.OnlineInstaller.csproj'
$publish = Join-Path $OutputRoot 'setup'
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=false `
    -o $publish
if ($LASTEXITCODE -ne 0) {
    throw "Installer publish failed with exit code $LASTEXITCODE"
}

$setupExe = Get-Item -LiteralPath (Join-Path $publish 'MLCCS-VideoSearch-Online-Setup.exe')
$publication = [ordered]@{
    version = $Version
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    setup = [ordered]@{
        file = $setupExe.Name
        size = $setupExe.Length
        sha256 = (Get-FileHash -LiteralPath $setupExe.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    manifest = [ordered]@{
        file = 'installer-manifest.json'
        size = (Get-Item -LiteralPath $manifestPath).Length
        sha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    components = @($components | ForEach-Object {
        [ordered]@{
            id = $_.id
            file = $_.archive
            size = $_.size
            sha256 = $_.sha256
        }
    })
}
$publication | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $OutputRoot 'publication-record.json') -Encoding utf8NoBOM

Write-Host "Installer payload ready: $OutputRoot"
