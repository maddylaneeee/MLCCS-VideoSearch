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
& $python -c @'
import sys
from pathlib import Path
sys.path.insert(0, str(Path(sys.argv[1]).resolve().parents[1]))
from mlccs_worker.downloader import DownloadManager, load_manifest

manifest, destination, prefix = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3]
items = [item for item in load_manifest(manifest) if item.id.startswith(prefix)]
if not items:
    raise SystemExit(f"Unknown locked model prefix: {prefix}")
manager = DownloadManager(destination)
for item in items:
    def progress(done, total, rate, item=item):
        print(f"{item.id}: {done}/{total} bytes ({done/max(total,1):.1%}) {rate/1024/1024:.1f} MiB/s", flush=True)
    path = manager.download(item, progress, lambda: False)
    print(f"VERIFIED {item.id} -> {path}", flush=True)
'@ $manifest $Destination $ModelPrefix
if ($LASTEXITCODE) { throw "Locked model installation failed with exit code $LASTEXITCODE" }
